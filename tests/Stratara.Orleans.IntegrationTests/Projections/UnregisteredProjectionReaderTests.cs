using System.Collections.Concurrent;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.Diagnostics;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Projections;
using Stratara.Projections.Abstractions;
using Stratara.Shared.Partitioning;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// Scenarios <em>A host is started without a projection it ran before</em>, <em>Facts are committed after a
/// projection's readers retired</em> and <em>A projection is registered again</em>: a host that ran two projections
/// leaves checkpoints and keep-alive reminders for both in every partition; started again with one of them removed,
/// the removed projection's readers are brought back by their reminders once, retire, and neither read nor refuse
/// anything, while their checkpoints stay; started a third time with the projection registered again, its readers
/// resume from those checkpoints and apply only what was committed while it was absent. A retired reader refuses a
/// pause and answers a wait as not reached.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class UnregisteredProjectionReaderTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int SiloPort = 11540;
    private const int GatewayPort = 30540;
    private const string UndeliveredTick = "Could not deliver reminder tick";
    private static readonly int PartitionCount = new CommitOrderOptions().PartitionCount;
    private static readonly string ReaderName = $"postgres-transaction-id/{PartitionCount}";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(12);

    [Fact]
    public async Task A_projection_removed_from_the_host_retires_its_readers_and_resumes_when_registered_again()
    {
        const string removed = nameof(RemovedProbeProjection);
        var tenantId = Guid.NewGuid();

        var first = new SeenLog();
        Dictionary<int, long> checkpoints;
        using (var app = await StartAsync(first, new CapturedLogs(), withRemoved: true, reset: true))
        {
            var streams = await AppendOnePerPartitionAsync(app.Services, tenantId);
            Assert.True(await WaitUntilAsync(() => Task.FromResult(first.Count(nameof(KeptProbeProjection)) == PartitionCount && first.Count(removed) == PartitionCount)),
                $"the projections did not apply the first facts: {first}");
            Assert.True(await WaitUntilAsync(async () => (await CheckpointsAsync(app.Services, removed)).Count == PartitionCount),
                "the removed projection did not record a checkpoint in every partition");
            checkpoints = await CheckpointsAsync(app.Services, removed);
            Assert.True(await WaitUntilAsync(async () => await RemindersAsync(app, removed) == PartitionCount), "the removed projection's keep-alives were not registered");
            Assert.Equal(PartitionCount, streams.Count);
            await app.StopAsync();
        }

        var second = new SeenLog();
        var logs = new CapturedLogs();
        List<Guid> whileAbsent;
        using (var app = await StartAsync(second, logs, withRemoved: false, reset: false))
        {
            Assert.True(await WaitUntilAsync(() => Task.FromResult(Retirements(logs, removed).Count == PartitionCount)),
                $"the removed projection's readers did not all retire: {string.Join(", ", Retirements(logs, removed))}");
            Assert.True(await WaitUntilAsync(async () => await RemindersAsync(app, removed) == 0), "the removed projection's keep-alives are still registered");

            whileAbsent = await AppendOnePerPartitionAsync(app.Services, tenantId);
            Assert.True(await WaitUntilAsync(() => Task.FromResult(second.Count(nameof(KeptProbeProjection)) == PartitionCount)),
                $"the remaining projection did not apply the facts committed after the retirement: {second}");
            await Task.Delay(Settle);

            Assert.Equal(PartitionCount, Retirements(logs, removed).Distinct().Count());
            Assert.Equal(PartitionCount, Retirements(logs, removed).Count);
            Assert.DoesNotContain(logs.Entries, entry =>
                entry.EventId is LogEvents.Orleans.EntryAttemptFailed or LogEvents.Orleans.PartitionStalled or LogEvents.Orleans.CatchUpFaulted or LogEvents.Orleans.StoreReaderStarted
                && entry.Message.Contains(removed, StringComparison.Ordinal));
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(UndeliveredTick, StringComparison.Ordinal) && entry.Message.Contains(removed, StringComparison.Ordinal));
            Assert.Equal(0, await RemindersAsync(app, removed));
            Assert.Equal(checkpoints, await CheckpointsAsync(app.Services, removed));

            var reader = app.Services.GetRequiredService<IGrainFactory>().GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(removed, 0));
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.PauseAsync(Guid.NewGuid(), TimeSpan.FromMinutes(1)));
            Assert.Equal(new StoreReaderProgress(false, StoreReaderState.Unregistered), await reader.WaitForAsync(checkpoints[0] + 1, TimeSpan.FromMilliseconds(200)));
            await app.StopAsync();
        }

        var third = new SeenLog();
        using (var app = await StartAsync(third, new CapturedLogs(), withRemoved: true, reset: false))
        {
            Assert.True(await WaitUntilAsync(() => Task.FromResult(third.Count(removed) == PartitionCount)),
                $"the projection registered again did not apply what was committed while it was absent: {third}");
            Assert.True(await WaitUntilAsync(async () => await RemindersAsync(app, removed) == PartitionCount), "the projection registered again has no keep-alives");
            await Task.Delay(Settle);

            Assert.Equal(PartitionCount, third.Count(removed));
            Assert.All(whileAbsent, streamId => Assert.True(third.Saw(removed, streamId), $"stream {streamId} was not applied"));
            Assert.Equal(0, third.Count(nameof(KeptProbeProjection)));
            await app.StopAsync();
        }
    }

    /// <summary>The partitions of every logged retirement of <paramref name="projection"/>'s readers, once per log line.</summary>
    private static List<int> Retirements(CapturedLogs logs, string projection) =>
    [
        .. logs.Entries
            .Where(entry => entry.EventId == LogEvents.Orleans.UnregisteredProjectionReaderRetired && entry.Message.Contains(projection, StringComparison.Ordinal))
            .Select(entry => Enumerable.Range(0, PartitionCount).Single(partition => entry.Message.Contains($"partition {partition} ", StringComparison.Ordinal))),
    ];

    /// <summary>One new stream in each partition, so every reader of every projection has something to read.</summary>
    private static async Task<List<Guid>> AppendOnePerPartitionAsync(IServiceProvider services, Guid tenantId)
    {
        var byPartition = new Dictionary<int, Guid>();
        while (byPartition.Count < PartitionCount)
        {
            var streamId = Guid.NewGuid();
            byPartition.TryAdd(PartitionMap.PartitionOf(BucketCalculator.GetBucketId(streamId), PartitionCount), streamId);
        }

        foreach (var streamId in byPartition.Values)
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.CreateAsync<Counter>(streamId, new CounterCreated(streamId));
            await events.SaveChangesAsync();
        }

        return [.. byPartition.Values];
    }

    private static async Task<Dictionary<int, long>> CheckpointsAsync(IServiceProvider services, string consumer)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>();
        var positions = new Dictionary<int, long>();
        for (var partition = 0; partition < PartitionCount; partition++)
        {
            if (await store.FindAsync(consumer, partition, ReaderName) is { } position and > 0)
            {
                positions[partition] = position;
            }
        }

        return positions;
    }

    /// <summary>How many of <paramref name="consumer"/>'s readers have a keep-alive registered.</summary>
    private static async Task<int> RemindersAsync(IHost host, string consumer)
    {
        var grains = host.Services.GetRequiredService<IGrainFactory>();
        var reminders = host.Services.GetRequiredService<IReminderTable>();
        var registered = 0;
        for (var partition = 0; partition < PartitionCount; partition++)
        {
            var grainId = grains.GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(consumer, partition)).GetGrainId();
            registered += (await reminders.ReadRows(grainId)).Reminders.Count;
        }

        return registered;
    }

    private async Task<IHost> StartAsync(SeenLog seen, CapturedLogs logs, bool withRemoved, bool reset)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_unregistered_reader_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.Logging.AddProvider(logs);
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, SiloPort, GatewayPort));
        builder.AddEventProjectionServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(postgres.ConnectionStringFor("poc_unregistered_reader_read"))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddSingleton(seen)
            .AddScoped<IProjection, KeptProbeProjection>()
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
            .AddStrataraProjectionGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(1);
                options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
            });
        if (withRemoved)
        {
            builder.Services.AddScoped<IProjection, RemovedProbeProjection>();
        }

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
                if (reset)
                {
                    await write.Database.ExecuteSqlRawAsync("DELETE FROM event_stream_entry");
                }
            }

            await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await read.Database.EnsureCreatedAsync();
            if (reset)
            {
                await read.Database.ExecuteSqlRawAsync("DELETE FROM projection_checkpoint");
            }
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    /// <summary>Which streams each probe projection saw.</summary>
    private sealed class SeenLog
    {
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> _seen = new(StringComparer.Ordinal);

        public void Record(string projection, Guid streamId) => _seen.GetOrAdd(projection, _ => new ConcurrentDictionary<Guid, byte>())[streamId] = 0;

        public bool Saw(string projection, Guid streamId) => _seen.TryGetValue(projection, out var seen) && seen.ContainsKey(streamId);

        public int Count(string projection) => _seen.TryGetValue(projection, out var seen) ? seen.Count : 0;

        public override string ToString() => string.Join(", ", _seen.Select(pair => $"{pair.Key}={pair.Value.Count}"));
    }

    private sealed class KeptProbeProjection(SeenLog seen) : IProjection
    {
        [UsedImplicitly]
        public Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken)
        {
            seen.Record(nameof(KeptProbeProjection), @event.StreamId);
            return Task.CompletedTask;
        }
    }

    private sealed class RemovedProbeProjection(SeenLog seen) : IProjection
    {
        [UsedImplicitly]
        public Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken)
        {
            seen.Record(nameof(RemovedProbeProjection), @event.StreamId);
            return Task.CompletedTask;
        }
    }

    /// <summary>Every log entry the host writes, with its event id and rendered message.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<(int EventId, string Message)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(Entries);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<(int EventId, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((eventId.Id, formatter(state, exception)));
        }
    }
}
