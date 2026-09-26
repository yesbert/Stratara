using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Outbox;
using Stratara.Diagnostics;
using Stratara.Orleans.Aggregates;

namespace Stratara.Orleans.Tests;

/// <summary>
/// A recorded command whose save committed its events but could not publish them is completed, not recorded as failed:
/// the drain would otherwise run it again and record its events a second time.
/// </summary>
public sealed class IntentCommittedNotPublishedTests
{
    private sealed class LoggerFactoryOf(RecordingLogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_command_that_committed_but_could_not_publish_is_completed_and_not_recorded_as_failed()
    {
        var intentId = Guid.NewGuid();
        var logger = new RecordingLogger();
        var intents = new Mock<ICommandIntentStore>();
        intents.Setup(store => store.TryRenewAsync(intentId, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var completed = new ConcurrentBag<Guid>();
        var queue = new IntentCompletionQueue(TimeSpan.FromMilliseconds(20), 64, (ids, _) =>
        {
            foreach (var id in ids)
            {
                completed.Add(id);
            }

            return Task.CompletedTask;
        });
        await queue.StartAsync(CancellationToken.None);
        var services = new ServiceCollection()
            .AddSingleton(intents.Object)
            .AddSingleton(TimeProvider.System)
            .AddSingleton(Options.Create(new OrleansDispatchOptions { IntentGrace = TimeSpan.FromMinutes(1) }))
            .AddSingleton<ILogger<IntentLease>>(new TypedLogger<IntentLease>(logger))
            .AddSingleton<ILoggerFactory>(new LoggerFactoryOf(logger))
            .AddSingleton(queue)
            .BuildServiceProvider();
        var lease = await IntentLease.StartAsync(services, intentId, claimedAt: null) ?? throw new InvalidOperationException("the lease was not started");

        await CommandExecution.RunIntentAsync(
            services,
            new AggregateCommandEnvelope("App.Commands.Approve", "{}", "{}"),
            intentId,
            lease,
            around: _ => throw new CommittedEventsNotPublishedException([Guid.NewGuid()], 1, new InvalidOperationException("the bus is down")));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!completed.Contains(intentId) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Contains(intentId, completed);
        intents.Verify(store => store.RecordFailureAsync(intentId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(logger.Entries, e => e.EventId.Id == LogEvents.Orleans.IntentCommittedNotPublished && e.Level == LogLevel.Error);
        await queue.StopAsync(CancellationToken.None);
    }
}
