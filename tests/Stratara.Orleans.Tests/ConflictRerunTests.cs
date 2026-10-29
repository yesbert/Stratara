using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Messaging;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Reflections;
using Stratara.Abstractions.Security;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// Scenarios <em>A heavy command's first attempt meets a conflict</em>, <em>The store cannot count conflicts</em> and
/// the conflict bound, at the command's execution: a conflict runs the command again in a scope of its own, inside the
/// hold its runner put around it, while the recorded conflicts leave room under the bound.
/// </summary>
public sealed class ConflictRerunTests
{
    [Fact]
    public async Task A_conflict_runs_the_command_again_in_a_scope_of_its_own_inside_the_hold_around_it()
    {
        var run = new Run(conflictsFirst: 1);
        run.Intents.Setup(store => store.CountConflictAsync(run.IntentId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        var holds = 0;

        await run.ExecuteAsync(async attempts =>
        {
            Interlocked.Increment(ref holds);
            await attempts();
        });

        Assert.Equal(1, holds);
        Assert.Equal(2, run.Scopes.Count);
        Assert.Equal(2, run.Scopes.Distinct().Count());
        run.Intents.Verify(store => store.CountConflictAsync(run.IntentId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        run.Intents.Verify(store => store.RecordFailureAsync(run.IntentId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(await run.CompletedAsync());
    }

    [Fact]
    public async Task A_store_that_cannot_count_conflicts_records_the_conflict_and_leaves_the_command_to_the_drain()
    {
        var run = new Run(conflictsFirst: 1);

        await Assert.ThrowsAsync<ConcurrencyException>(() => run.ExecuteAsync());

        Assert.Single(run.Scopes);
        run.Intents.Verify(store => store.RecordConflictAsync(run.IntentId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        run.Intents.Verify(store => store.RecordFailureAsync(run.IntentId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_command_whose_conflicts_reached_the_bound_is_not_run_again()
    {
        var run = new Run(conflictsFirst: 5, maxConflictRequeues: 3);
        var counted = 2;
        run.Intents.Setup(store => store.CountConflictAsync(run.IntentId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => ++counted);

        await Assert.ThrowsAsync<ConcurrencyException>(() => run.ExecuteAsync());

        Assert.Equal(2, run.Scopes.Count);
        run.Intents.Verify(store => store.CountConflictAsync(run.IntentId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        run.Intents.Verify(store => store.RecordFailureAsync(run.IntentId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_failure_other_than_a_conflict_is_recorded_once_and_not_run_again()
    {
        var run = new Run(conflictsFirst: 0) { Failure = new InvalidOperationException("the handler fails") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.ExecuteAsync());

        Assert.Single(run.Scopes);
        run.Intents.Verify(store => store.RecordFailureAsync(run.IntentId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    public sealed record RerunProbe(Guid ProbeId) : ICommand;

    /// <summary>One per scope, telling the attempts apart.</summary>
    private sealed class ScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    /// <summary>A recorded command's run against a store and a mediator the test controls.</summary>
    private sealed class Run
    {
        private readonly int _conflictsFirst;
        private readonly ConcurrentBag<Guid> _completed = [];
        private readonly IntentCompletionQueue _queue;
        private readonly ServiceProvider _services;

        public Run(int conflictsFirst, int maxConflictRequeues = 100)
        {
            _conflictsFirst = conflictsFirst;
            Intents.Setup(store => store.TryRenewAsync(IntentId, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
            _queue = new IntentCompletionQueue(TimeSpan.FromMilliseconds(20), 64, (ids, _) =>
            {
                foreach (var id in ids)
                {
                    _completed.Add(id);
                }

                return Task.CompletedTask;
            });
            var types = new Mock<ITrustedTypeResolver>();
            types.Setup(resolver => resolver.Resolve(nameof(RerunProbe))).Returns(typeof(RerunProbe));
            var serializer = new Mock<ISecureJsonSerializer>();
            serializer.Setup(s => s.DeserializeAsync(It.IsAny<string>(), typeof(RerunProbe), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RerunProbe(Guid.NewGuid()));
            _services = new ServiceCollection()
                .AddResiliencePipelines()
                .AddSingleton(Intents.Object)
                .AddSingleton(TimeProvider.System)
                .AddSingleton(Options.Create(new OrleansDispatchOptions { IntentGrace = TimeSpan.FromMinutes(1) }))
                .AddSingleton(Options.Create(new MessageRetryOptions { MaxConflictRequeues = maxConflictRequeues }))
                .AddSingleton<ILogger<IntentLease>>(new TypedLogger<IntentLease>(new RecordingLogger()))
                .AddSingleton<ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
                .AddSingleton(_queue)
                .AddSingleton(types.Object)
                .AddSingleton(serializer.Object)
                .AddSingleton(new Mock<ISessionContextProvider>().Object)
                .AddScoped<ScopeMarker>()
                .AddScoped<IMediator>(scope => Mediator(scope.GetRequiredService<ScopeMarker>()))
                .BuildServiceProvider();
        }

        public Guid IntentId { get; } = Guid.CreateVersion7();

        public Mock<ICommandIntentStore> Intents { get; } = new() { CallBase = true };

        public ConcurrentQueue<Guid> Scopes { get; } = new();

        public Exception? Failure { get; init; }

        public async Task ExecuteAsync(Func<Func<Task>, Task>? around = null)
        {
            await _queue.StartAsync(CancellationToken.None);
            var scope = _services.CreateScope();
            var lease = await IntentLease.StartAsync(scope.ServiceProvider, IntentId, claimedAt: null) ?? throw new InvalidOperationException("the lease was not started");
            var session = new SessionContext("correlation", null, null, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null);
            await CommandExecution.RunIntentAsync(scope.ServiceProvider, new AggregateCommandEnvelope(nameof(RerunProbe), "{}", JsonSerializer.Serialize(session)), IntentId, lease, around);
        }

        public async Task<bool> CompletedAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!_completed.Contains(IntentId) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            await _queue.StopAsync(CancellationToken.None);
            return _completed.Contains(IntentId);
        }

        private IMediator Mediator(ScopeMarker marker)
        {
            var mediator = new Mock<IMediator>();
            mediator.Setup(m => m.HandleAsync(It.IsAny<RerunProbe>(), It.IsAny<CancellationToken>())).Returns(() =>
            {
                Scopes.Enqueue(marker.Id);
                if (Failure is { } failure)
                {
                    return Task.FromException(failure);
                }

                return Scopes.Count <= _conflictsFirst
                    ? Task.FromException(new ConcurrencyException(Guid.NewGuid(), nameof(RerunProbe)))
                    : Task.CompletedTask;
            });
            return mediator.Object;
        }
    }
}
