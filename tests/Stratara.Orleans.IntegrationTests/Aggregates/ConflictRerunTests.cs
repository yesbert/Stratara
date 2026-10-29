using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Orleans.Aggregates;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// Scenario <em>A command's first attempt meets a conflict</em>: a recorded command whose handler reports the event
/// store's conflict on its first run runs again within a second, in a unit of work of its own, and completes — long
/// before the grace after which the drain would resume it.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ConflictRerunTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_command_whose_first_attempt_conflicts_runs_again_at_once_in_a_unit_of_work_of_its_own()
    {
        var probes = new RecordedIntentProbes();
        var conflicts = new ConflictOnceLog();
        var store = postgres.ConnectionStringFor("poc_conflict_rerun");
        using var host = await RecordedIntentHost.StartAsync(new RecordedIntentSettings(
            store, postgres.ConnectionStringFor("poc_orleans"), redis.ConnectionString, rabbit.ConnectionString, 11565, 30565, probes,
            PollingInterval: TimeSpan.FromSeconds(1), BatchSize: 100, Services: services => services
                .AddSingleton(conflicts)
                .AddScoped<AttemptScope>()
                .AddScoped<ICommandHandler<ConflictOnceProbe>, ConflictOnceProbeHandler>()
                .AddTrustedType<ConflictOnceProbe>()
                .Configure<OrleansDispatchOptions>(options => options.IntentGrace = Grace)));
        var probe = Guid.NewGuid();

        Guid intent;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            intent = await scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(new ConflictOnceProbe(Guid.NewGuid(), probe));
        }

        Assert.True(
            await RecordedIntentHost.WaitUntilAsync(() => Task.FromResult(conflicts.Succeeded.ContainsKey(probe)), TimeSpan.FromSeconds(20)),
            $"the command did not succeed; it ran {conflicts.Scopes(probe).Count} times");
        var conflictedAt = conflicts.Conflicted[probe];
        var succeededAt = conflicts.Succeeded[probe];

        Assert.True(succeededAt - conflictedAt < TimeSpan.FromSeconds(1), $"the command ran again {succeededAt - conflictedAt} after its conflict");
        Assert.Equal(2, conflicts.Scopes(probe).Count);
        Assert.Equal(2, conflicts.Scopes(probe).Distinct().Count());
        Assert.Equal([true, true], conflicts.OnTheActivation(probe));
        Assert.True(
            await RecordedIntentHost.WaitUntilAsync(
                async () => await RecordedIntentHost.ScalarAsync<int>(store, "SELECT count(*)::int FROM outbox_entry WHERE id = @id", ("id", intent)) == 0,
                TimeSpan.FromSeconds(10)),
            "the command's record was not completed");
        await host.StopAsync();
    }

    /// <summary>When each probe conflicted and succeeded, and the scope each run had.</summary>
    public sealed class ConflictOnceLog
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<Guid>> _scopes = new();

        public ConcurrentDictionary<Guid, DateTimeOffset> Conflicted { get; } = new();

        public ConcurrentDictionary<Guid, DateTimeOffset> Succeeded { get; } = new();

        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<bool>> _onTheActivation = new();

        public IReadOnlyCollection<Guid> Scopes(Guid probe) => _scopes.TryGetValue(probe, out var scopes) ? [.. scopes] : [];

        /// <summary>For each run, whether it ran on the aggregate activation's scheduler rather than the thread pool's.</summary>
        public IReadOnlyCollection<bool> OnTheActivation(Guid probe) => _onTheActivation.TryGetValue(probe, out var runs) ? [.. runs] : [];

        public int Ran(Guid probe, Guid scope)
        {
            _onTheActivation.GetOrAdd(probe, _ => new ConcurrentQueue<bool>()).Enqueue(TaskScheduler.Current != TaskScheduler.Default);
            var scopes = _scopes.GetOrAdd(probe, _ => new ConcurrentQueue<Guid>());
            scopes.Enqueue(scope);
            return scopes.Count;
        }
    }

    /// <summary>One per scope: tells the runs of a command apart by the unit of work they ran in.</summary>
    public sealed class AttemptScope
    {
        public Guid Id { get; } = Guid.NewGuid();
    }
}

/// <summary>A command whose handler reports the event store's conflict on its first run and succeeds on the next.</summary>
public sealed record ConflictOnceProbe(Guid AggregateId, Guid ProbeId) : ICommand, IAggregateScopedCommand;

public sealed class ConflictOnceProbeHandler(ConflictRerunTests.ConflictOnceLog log, ConflictRerunTests.AttemptScope scope) : ICommandHandler<ConflictOnceProbe>
{
    public Task HandleAsync(ConflictOnceProbe command, CancellationToken cancellationToken)
    {
        if (log.Ran(command.ProbeId, scope.Id) == 1)
        {
            log.Conflicted[command.ProbeId] = DateTimeOffset.UtcNow;
            throw new ConcurrencyException(command.AggregateId, nameof(ConflictOnceProbe));
        }

        log.Succeeded[command.ProbeId] = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }
}
