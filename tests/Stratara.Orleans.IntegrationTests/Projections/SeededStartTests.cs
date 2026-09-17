using System.Collections.Concurrent;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Session;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// Scenarios <em>A populated store is seeded before the first start</em> and <em>A projection is added after the
/// seeding</em>: a store with history, seeded from the host's composition while no silo runs, starts without
/// applying anything old and applies everything new; a projection registered afterwards without a checkpoint reads
/// from the beginning. The control run shows the baseline the seeding exists for: without it, the first start applies
/// the whole history.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class SeededStartTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int History = 3;
    private const int New = 2;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(6);

    [Fact]
    public async Task A_seeded_first_start_applies_nothing_old_and_everything_new_and_a_late_projection_reads_from_the_beginning()
    {
        var store = postgres.ConnectionStringFor("poc_seeded_store");
        var read = postgres.ConnectionStringFor("poc_seeded_read");
        var tenantId = Guid.NewGuid();
        var history = await PopulateAsync(store, read, tenantId, History);
        var seen = new SeenLog();

        using (var app = await BuildAsync(seen, store, read, siloPort: 11319, gatewayPort: 30209, withLateProjection: false))
        {
            StoreReaderSeedingReport report;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                report = await scope.ServiceProvider.GetRequiredService<IStoreReaderSeeding>().SeedAtHeadAsync();
            }

            Assert.Equal(new StoreReaderSeedingReport(Seeded: 16, Existing: 0), report);
            using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await app.StartAsync(startTimeout.Token);

            var fresh = new List<Guid>();
            for (var i = 0; i < New; i++)
            {
                fresh.Add(await AppendAsync(app.Services, tenantId));
            }

            Assert.True(await WaitUntilAsync(() => fresh.All(seen.Saw)), $"the seeded projection did not apply the new facts: {seen}");
            await Task.Delay(Settle);
            Assert.All(history, streamId => Assert.False(seen.Saw(streamId), $"the seeded projection applied old stream {streamId}"));
            Assert.Equal(New, seen.Count(nameof(SeedProbeProjection)));
            await app.StopAsync();
        }

        using (var later = await BuildAsync(seen, store, read, siloPort: 11319, gatewayPort: 30209, withLateProjection: true))
        {
            using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await later.StartAsync(startTimeout.Token);

            Assert.True(await WaitUntilAsync(() => seen.Count(nameof(LateProbeProjection)) == History + New), $"the late projection did not read the whole store: {seen}");
            await Task.Delay(Settle);
            Assert.Equal(History + New, seen.Count(nameof(LateProbeProjection)));
            Assert.Equal(New, seen.Count(nameof(SeedProbeProjection)));
            await later.StopAsync();
        }
    }

    /// <summary>The baseline: a first start without seeding applies the history.</summary>
    [Fact]
    public async Task Without_seeding_the_first_start_applies_the_history()
    {
        var store = postgres.ConnectionStringFor("poc_unseeded_store");
        var read = postgres.ConnectionStringFor("poc_unseeded_read");
        var tenantId = Guid.NewGuid();
        var history = await PopulateAsync(store, read, tenantId, History);
        var seen = new SeenLog();

        using var app = await BuildAsync(seen, store, read, siloPort: 11320, gatewayPort: 30210, withLateProjection: false);
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);

        Assert.True(await WaitUntilAsync(() => history.All(seen.Saw)), $"the unseeded projection did not apply the history: {seen}");
        await app.StopAsync();
    }

    /// <summary>The history: appended through the event source from a plain host without the execution model, before any reader exists.</summary>
    private async Task<List<Guid>> PopulateAsync(string store, string read, Guid tenantId, int streams)
    {
        await PostgresTimerHostSchema.EnsureDatabaseAsync(store);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(read);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = store,
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.AddBackendServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(read)
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .Configure<CommitOrderOptions>(options => options.MaintainPartitionCounter = false);
        using var writer = builder.Build();
        await using (var scope = writer.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
                await write.Database.ExecuteSqlRawAsync("DELETE FROM event_stream_entry");
            }

            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
            await context.Database.ExecuteSqlRawAsync("DELETE FROM projection_checkpoint");
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await writer.StartAsync(startTimeout.Token);
        var ids = new List<Guid>();
        for (var i = 0; i < streams; i++)
        {
            ids.Add(await AppendAsync(writer.Services, tenantId));
        }

        await writer.StopAsync();
        return ids;
    }

    private async Task<IHost> BuildAsync(SeenLog seen, string store, string read, int siloPort, int gatewayPort, bool withLateProjection)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = store,
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddEventProjectionServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(read)
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddSingleton(seen)
            .AddScoped<IProjection, SeedProbeProjection>()
            .Configure<CommitOrderOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
            .AddStrataraProjectionGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(1);
                options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
            });
        if (withLateProjection)
        {
            builder.Services.AddScoped<IProjection, LateProbeProjection>();
        }

        return builder.Build();
    }

    private static async Task<Guid> AppendAsync(IServiceProvider services, Guid tenantId)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        var streamId = Guid.NewGuid();
        await events.CreateAsync<Counter>(streamId, new CounterCreated(streamId));
        await events.SaveChangesAsync();
        return streamId;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }
}

/// <summary>Which stream each probe projection saw.</summary>
public sealed class SeenLog
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> _seen = new(StringComparer.Ordinal);

    public void Record(string projection, Guid streamId) => _seen.GetOrAdd(projection, _ => new ConcurrentDictionary<Guid, byte>())[streamId] = 0;

    public bool Saw(Guid streamId) => _seen.TryGetValue(nameof(SeedProbeProjection), out var seen) && seen.ContainsKey(streamId);

    public int Count(string projection) => _seen.TryGetValue(projection, out var seen) ? seen.Count : 0;

    public override string ToString() => string.Join(", ", _seen.Select(pair => $"{pair.Key}={pair.Value.Count}"));
}

public sealed class SeedProbeProjection(SeenLog seen) : IProjection
{
    [UsedImplicitly]
    public Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken)
    {
        seen.Record(nameof(SeedProbeProjection), @event.StreamId);
        return Task.CompletedTask;
    }
}

public sealed class LateProbeProjection(SeenLog seen) : IProjection
{
    [UsedImplicitly]
    public Task HandleAsync(IEvent<CounterCreated> @event, CancellationToken cancellationToken)
    {
        seen.Record(nameof(LateProbeProjection), @event.StreamId);
        return Task.CompletedTask;
    }
}
