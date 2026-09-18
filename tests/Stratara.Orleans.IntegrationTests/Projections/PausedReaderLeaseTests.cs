using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// Change let-a-paused-reader-always-come-back on the PostgreSQL store. Scenario <em>The process rebuilding a projection
/// dies</em>: readers paused by a pauser that never renews nor resumes come back by themselves once the lease has passed,
/// log the lapse, and apply what was committed meanwhile. Scenario <em>A reader resumes while its read model is being
/// emptied</em>: a rebuild whose pause lapses while it holds its truncation — its renewals never fire — lets the readers
/// apply and advance before the model is emptied, and the model still holds every fact once the rebuild has finished.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class PausedReaderLeaseTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int Streams = 4;
    private const int FactsPerStream = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    private static readonly string Consumer = nameof(RebuildProbeProjection);

    [Fact]
    public async Task Readers_whose_pauser_died_resume_after_the_lease_and_log_the_lapse()
    {
        var control = new RebuildProbeControl();
        var logs = new CapturedLogs();
        using var app = await StartAsync(control, logs, "poc_dead_pauser_read", lease: null, siloPort: 11461, gatewayPort: 30461);
        var tenantId = Guid.NewGuid();
        var expected = await AppendFactsAsync(app.Services, tenantId);
        Assert.True(await WaitUntilAsync(async () => await CountAsync(app.Services) == expected), "the model was not built before the pause");

        var partitions = new CommitOrderOptions().PartitionCount;
        var grains = app.Services.GetRequiredService<IGrainFactory>();
        var deadPauser = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, partitions)
            .Select(partition => grains.GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(Consumer, partition)).PauseAsync(deadPauser, TimeSpan.FromSeconds(6))));

        var late = Guid.NewGuid();
        await AppendAsync(app.Services, tenantId, late, new CounterCreated(late), create: true);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(expected, await CountAsync(app.Services));

        Assert.True(
            await WaitUntilAsync(async () => await CountAsync(app.Services) == expected + 1),
            "the readers did not resume after the dead pauser's lease had passed");
        Assert.True(
            await WaitUntilAsync(() => Task.FromResult(LapsedPartitions(logs).Count == partitions)),
            $"the lapse was logged for {LapsedPartitions(logs).Count} of {partitions} partitions");

        await app.StopAsync();
    }

    [Fact]
    public async Task A_reader_that_resumes_while_its_model_is_emptied_leaves_the_model_complete()
    {
        var control = new RebuildProbeControl();
        var logs = new CapturedLogs();
        var neverRenewed = new StoreReaderLease(TimeSpan.FromSeconds(2), new NeverTickingClock());
        using var app = await StartAsync(control, logs, "poc_early_resume_read", neverRenewed, siloPort: 11462, gatewayPort: 30462);
        var tenantId = Guid.NewGuid();
        var expected = await AppendFactsAsync(app.Services, tenantId);
        Assert.True(await WaitUntilAsync(async () => await CountAsync(app.Services) == expected), "the model was not built before the rebuild");

        control.HoldTruncationNumber = 1;
        var rebuild = app.Services.GetRequiredService<IProjectionRebuilder>().RebuildAsync(Consumer);
        Assert.True(await WaitUntilAsync(() => Task.FromResult(control.Truncations == 1)), "the rebuild never reached the truncation");
        Assert.True(
            await WaitUntilAsync(async () => await CheckpointsAsync(app.Services) > 0),
            "no reader resumed and advanced while the truncation was held");

        control.HoldTruncation.TrySetResult();
        await rebuild;

        Assert.True(
            await WaitUntilAsync(async () => await CountAsync(app.Services) == expected),
            $"after a rebuild whose readers resumed early the model holds {await CountAsync(app.Services)} of {expected} rows");
        await Task.Delay(Settle);
        Assert.Equal(expected, await CountAsync(app.Services));
        Assert.NotEmpty(LapsedPartitions(logs));

        await app.StopAsync();
    }

    private static HashSet<string> LapsedPartitions(CapturedLogs logs) =>
    [
        .. logs.Entries
            .Where(entry => entry.EventId == LogEvents.Orleans.StoreReaderPauseLapsed && entry.Message.StartsWith($"Store reader for {Consumer} on partition ", StringComparison.Ordinal))
            .Select(entry => entry.Message.Split(' ')[6]),
    ];

    private static async Task<int> AppendFactsAsync(IServiceProvider services, Guid tenantId)
    {
        foreach (var streamId in Enumerable.Range(0, Streams).Select(_ => Guid.NewGuid()))
        {
            await AppendAsync(services, tenantId, streamId, new CounterCreated(streamId), create: true);
            for (var i = 1; i < FactsPerStream; i++)
            {
                await AppendAsync(services, tenantId, streamId, new CounterIncremented(streamId, i), create: false);
            }
        }

        return Streams * FactsPerStream;
    }

    /// <summary>The sum of the probe's checkpoints over every partition: above zero once any reader applied after the reset.</summary>
    private static async Task<long> CheckpointsAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var checkpoints = scope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>();
        var reader = scope.ServiceProvider.GetRequiredService<ICommittedPositionReader>().Name;
        var sum = 0L;
        for (var partition = 0; partition < new CommitOrderOptions().PartitionCount; partition++)
        {
            sum += await checkpoints.GetAsync(Consumer, partition, reader);
        }

        return sum;
    }

    private async Task<IHost> StartAsync(RebuildProbeControl control, CapturedLogs logs, string readDatabase, StoreReaderLease? lease, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor($"{readDatabase}_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.Logging.AddProvider(logs);
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddEventProjectionServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(postgres.ConnectionStringFor(readDatabase))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<CounterIncremented>()
            .AddScoped<IProjection, RebuildProbeProjection>()
            .AddSingleton(control)
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
            .AddStrataraProjectionGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(1);
                options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
            });
        if (lease is not null)
        {
            builder.Services.Replace(ServiceDescriptor.Singleton(lease));
        }

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
            }

            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
            await context.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS poc_rebuild_probe (stream_id uuid NOT NULL, version bigint NOT NULL, PRIMARY KEY (stream_id, version))");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM poc_rebuild_probe");
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task AppendAsync(IServiceProvider services, Guid tenantId, Guid streamId, object @event, bool create)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        if (create)
        {
            await events.CreateAsync<Counter>(streamId, @event);
        }
        else
        {
            await events.AppendAsync<Counter>(streamId, @event);
        }

        await events.SaveChangesAsync();
    }

    private static async Task<long> CountAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
        return await context.Database.SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM poc_rebuild_probe").SingleAsync();
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

            await Task.Delay(200);
        }

        return false;
    }

    /// <summary>A clock whose timers never fire: a hold renewing on it never renews, as a rebuilder stalled past its lease.</summary>
    private sealed class NeverTickingClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Silent();

        private sealed class Silent : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
