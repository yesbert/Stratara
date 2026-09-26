using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Persistence;
using Stratara.Diagnostics;
using Stratara.Infrastructure.EventSourcing;
using Stratara.Testing.EntityFrameworkCore;
using Xunit;

namespace Stratara.Infrastructure.Tests.EventSourcing;

/// <summary>
/// <c>aggregate-rehydration</c> → a snapshot captures only committed events: a save that fails leaves no snapshot
/// behind, and a snapshot that cannot be written does not fail a save whose events are committed. Against the SQLite
/// test store.
/// </summary>
public class SnapshotCommitTests
{
    public sealed class Counter
    {
        public int Value { get; set; }

        public void Apply(CounterAdded e) => Value += e.By;
    }

    public sealed record CounterAdded(int By);

    private sealed class SwitchableSnapshotStrategy : ISnapshotStrategy
    {
        public bool Enabled { get; set; }

        public bool ShouldSnapshot(Type aggregateType, long currentVersion, long lastSnapshotVersion) => Enabled;
    }

    private sealed class FailingSnapshotService : ISnapshotService
    {
        public Task AddSnapshotIfNeededAsync(IEnumerable<EventStreamEntry> eventStreamEntries, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the snapshot table is unavailable");
    }

    private static EventStoreTestHost CreateHost(Action<IServiceCollection> configure) =>
        EventStoreTestHost.Create(services =>
        {
            services.AddTrustedType<Counter>().AddTrustedType<CounterAdded>();
            configure(services);
        });

    private static async Task<Snapshot?> LatestSnapshotAsync(EventStoreTestHost host, Guid streamId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync();
        return await unitOfWork.CreateSnapshotRepository(transaction).GetAsync(streamId, typeof(Counter).AssemblyQualifiedName!);
    }

    [Fact]
    public async Task A_save_that_loses_a_race_leaves_no_snapshot_of_its_batch()
    {
        var strategy = new SwitchableSnapshotStrategy();
        await using var host = CreateHost(services => services.AddSingleton<ISnapshotStrategy>(strategy));
        var streamId = Guid.CreateVersion7();
        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Counter>(streamId, new CounterAdded(1));
            await events.SaveChangesAsync();
        });
        strategy.Enabled = true;

        await using var loserScope = host.Services.CreateAsyncScope();
        await using var winnerScope = host.Services.CreateAsyncScope();
        var loser = loserScope.ServiceProvider.GetRequiredService<IEventSource>();
        var winner = winnerScope.ServiceProvider.GetRequiredService<IEventSource>();
        await loser.AppendAsync<Counter>(streamId, new CounterAdded(10));
        await loser.AppendAsync<Counter>(streamId, new CounterAdded(100));
        await winner.AppendAsync<Counter>(streamId, new CounterAdded(1000));

        await winner.SaveChangesAsync();
        await Assert.ThrowsAsync<ConcurrencyException>(() => loser.SaveChangesAsync());

        Assert.Equal(2, (await LatestSnapshotAsync(host, streamId))?.Version);
        Assert.Equal(1001, (await host.AggregateAsync<Counter>(streamId))?.Value);
    }

    [Fact]
    public async Task A_snapshot_that_cannot_be_written_does_not_fail_a_committed_save()
    {
        await using var host = CreateHost(services => services.AddSingleton<ISnapshotService>(new FailingSnapshotService()));
        var streamId = Guid.CreateVersion7();

        await host.ExecuteAsync(async events =>
        {
            await events.CreateAsync<Counter>(streamId, new CounterAdded(1));
            await events.SaveChangesAsync();
        });

        Assert.Equal(1, (await host.AggregateAsync<Counter>(streamId))?.Value);
        Assert.Single(host.Outbox.Bundles);
    }

    private sealed class CapturingLogger : ILogger<EventSource>
    {
        public List<(int Id, LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((eventId.Id, logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task A_snapshot_that_cannot_be_written_is_logged_naming_the_stream()
    {
        await using var host = CreateHost(_ => { });
        var streamId = Guid.CreateVersion7();
        var logger = new CapturingLogger();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var events = (IEventSource)ActivatorUtilities.CreateInstance(
                scope.ServiceProvider, typeof(EventSource), new FailingSnapshotService(), logger);
            await events.CreateAsync<Counter>(streamId, new CounterAdded(1));
            await events.SaveChangesAsync();
        }

        var entry = Assert.Single(logger.Entries, e => e.Id == LogEvents.EventStore.SnapshotFailed);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(streamId.ToString(), entry.Message, StringComparison.Ordinal);
    }
}
