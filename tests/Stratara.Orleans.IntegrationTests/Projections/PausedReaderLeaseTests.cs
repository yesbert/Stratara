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

    /// <summary>
    /// Review finding on this change: a reader whose pause lapsed reads from the first reset, applies the first entry of
    /// its batch, and is still applying the rest when the model is emptied. The second reset writes the same beginning
    /// the reader started from, so without quiescing its advance would be accepted after the reset and the entry it
    /// applied before the truncation would be missing. The rebuild waits for that batch before the second reset.
    /// </summary>
    [Fact]
    public async Task A_batch_in_flight_across_the_truncation_leaves_the_model_complete()
    {
        var control = new RebuildProbeControl();
        var logs = new CapturedLogs();
        var neverRenewed = new StoreReaderLease(TimeSpan.FromSeconds(2), new NeverTickingClock());
        using var app = await StartAsync(control, logs, "poc_spanning_batch_read", neverRenewed, siloPort: 11464, gatewayPort: 30464);
        var expected = await AppendFactsAsync(app.Services, Guid.NewGuid(), streams: 1);
        Assert.True(await WaitUntilAsync(async () => await CountAsync(app.Services) == expected), "the model was not built before the rebuild");

        control.HoldTruncationNumber = 1;
        control.HoldApplicationNumber = 2;
        var rebuild = app.Services.GetRequiredService<IProjectionRebuilder>().RebuildAsync(Consumer);
        Assert.True(await WaitUntilAsync(() => Task.FromResult(control.Truncations == 1)), "the rebuild never reached the truncation");
        await control.ApplicationHeld.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        control.HoldTruncation.TrySetResult();
        Assert.True(await WaitUntilAsync(async () => await CountAsync(app.Services) == 0), "the truncation did not empty the model");
        await Task.Delay(TimeSpan.FromSeconds(2));
        control.HoldApplication.TrySetResult();
        await rebuild;

        Assert.True(
            await WaitUntilAsync(async () => await CountAsync(app.Services) == expected),
            $"after a batch in flight across the truncation the model holds {await CountAsync(app.Services)} of {expected} rows");
        await Task.Delay(Settle);
        Assert.Equal(expected, await CountAsync(app.Services));

        await app.StopAsync();
    }

    /// <summary>
    /// The reader's side of a pause it does not hold: a resume for a pauser it never held releases nothing; a renewal
    /// delivered after its pauser's resume pauses nothing; a renewal of a pauser it has not seen — its activation moved,
    /// or the pause lapsed — pauses it until that pauser resumes.
    /// </summary>
    [Fact]
    public async Task A_resume_or_a_renewal_for_a_pauser_the_reader_does_not_hold()
    {
        var control = new RebuildProbeControl();
        var logs = new CapturedLogs();
        using var app = await StartAsync(control, logs, "poc_unknown_pauser_read", lease: null, siloPort: 11465, gatewayPort: 30465);
        var tenantId = Guid.NewGuid();
        long rows = await AppendFactsAsync(app.Services, tenantId, streams: 1);
        Assert.True(await WaitUntilAsync(async () => await CountAsync(app.Services) == rows), "the model was not built before the pauses");
        var grains = Enumerable.Range(0, new CommitOrderOptions().PartitionCount)
            .Select(partition => app.Services.GetRequiredService<IGrainFactory>().GetGrain<IProjectionGrain>(StoreReaderGrainKey.Of(Consumer, partition)))
            .ToList();
        var lease = TimeSpan.FromMinutes(1);
        var held = Guid.NewGuid();

        await Task.WhenAll(grains.Select(grain => grain.PauseAsync(held, lease)));
        await Task.WhenAll(grains.Select(grain => grain.ResumeAsync(Guid.NewGuid())));
        rows = await AppendAndExpectAsync(app.Services, tenantId, rows, arrives: false);

        await Task.WhenAll(grains.Select(grain => grain.ResumeAsync(held)));
        Assert.True(await WaitUntilAsync(async () => await CountAsync(app.Services) == rows, Prompt), "the held pauser's resume did not start the readers");

        await Task.WhenAll(grains.Select(grain => grain.RenewPauseAsync(held, lease)));
        rows = await AppendAndExpectAsync(app.Services, tenantId, rows, arrives: true);

        var unseen = Guid.NewGuid();
        await Task.WhenAll(grains.Select(grain => grain.RenewPauseAsync(unseen, lease)));
        rows = await AppendAndExpectAsync(app.Services, tenantId, rows, arrives: false);
        await Task.WhenAll(grains.Select(grain => grain.ResumeAsync(unseen)));
        Assert.True(await WaitUntilAsync(async () => await CountAsync(app.Services) == rows, Prompt), "the renewing pauser's resume did not start the readers");
        Assert.Empty(LapsedPartitions(logs));

        await app.StopAsync();
    }

    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(15);

    /// <summary>Appends one fact and checks whether it reaches the model promptly or stays out of it for a while; returns the rows expected once it does.</summary>
    private static async Task<long> AppendAndExpectAsync(IServiceProvider services, Guid tenantId, long rows, bool arrives)
    {
        var streamId = Guid.NewGuid();
        await AppendAsync(services, tenantId, streamId, new CounterCreated(streamId), create: true);
        if (arrives)
        {
            Assert.True(await WaitUntilAsync(async () => await CountAsync(services) == rows + 1, Prompt), "a fact did not reach the model while no pauser held the readers");
        }
        else
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.Equal(rows, await CountAsync(services));
        }

        return rows + 1;
    }

    private static HashSet<string> LapsedPartitions(CapturedLogs logs) =>
    [
        .. logs.Entries
            .Where(entry => entry.EventId == LogEvents.Orleans.StoreReaderPauseLapsed && entry.Message.StartsWith($"Store reader for {Consumer} on partition ", StringComparison.Ordinal))
            .Select(entry => entry.Message.Split(' ')[6]),
    ];

    private static async Task<int> AppendFactsAsync(IServiceProvider services, Guid tenantId, int streams = Streams)
    {
        foreach (var streamId in Enumerable.Range(0, streams).Select(_ => Guid.NewGuid()))
        {
            await AppendAsync(services, tenantId, streamId, new CounterCreated(streamId), create: true);
            for (var i = 1; i < FactsPerStream; i++)
            {
                await AppendAsync(services, tenantId, streamId, new CounterIncremented(streamId, i), create: false);
            }
        }

        return streams * FactsPerStream;
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

    private static Task<bool> WaitUntilAsync(Func<Task<bool>> condition) => WaitUntilAsync(condition, Timeout);

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
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
