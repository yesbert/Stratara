using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Projections;
using Stratara.Abstractions.Session;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Replay;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Projections.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Projections;

/// <summary>
/// A replay on a host that keeps the read models it empties ends in one of two states on the PostgreSQL read store: the
/// rebuilt read models, or exactly the ones it started from. The preservation's mechanics — the abandoned copy, the copy
/// kept across a second replay, the refusals, the sequences — are driven directly through <see cref="IReadModelPreservation"/>.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ReplayRestoreTests(PostgreSqlFixture postgres, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task A_replay_that_fails_after_truncating_leaves_the_read_models_partial_without_the_registration()
    {
        using var app = await BuildAsync("poc_restore_without", restore: false);
        var streams = await SeedAsync(app.Services, count: 3);
        await ReplayAsync(app.Services);
        Assert.Equal(3, (await ViewsAsync(app.Services)).Count);

        app.Services.GetRequiredService<ProjectionProbeControl>().Poisoned.TryAdd(streams[^1], 0);
        var outcome = await ReplayAsync(app.Services);

        Assert.Equal(ReplayResult.Failed, outcome.Result);
        Assert.False(outcome.ReadModelsRestored);
        Assert.True((await ViewsAsync(app.Services)).Count < 3);
    }

    [Fact]
    public async Task A_replay_that_fails_restores_the_views_the_checkpoints_and_the_forgotten_tenants()
    {
        using var app = await BuildAsync("poc_restore_fails", restore: true);
        var streams = await SeedAsync(app.Services, count: 3);
        await ReplayAsync(app.Services);
        await SeedFrameworkTablesAsync(app.Services);
        var before = await SnapshotAsync(app.Services);

        app.Services.GetRequiredService<ProjectionProbeControl>().Poisoned.TryAdd(streams[^1], 0);
        var outcome = await ReplayAsync(app.Services);

        Assert.Equal(ReplayResult.Failed, outcome.Result);
        Assert.True(outcome.ReadModelsRestored);
        Assert.Equal(before, await SnapshotAsync(app.Services));
        Assert.Equal(0, await PreservedCountAsync(app.Services));
    }

    [Fact]
    public async Task A_replay_that_succeeds_leaves_no_copy()
    {
        using var app = await BuildAsync("poc_restore_succeeds", restore: true);
        await SeedAsync(app.Services, count: 2);

        var outcome = await ReplayAsync(app.Services);

        Assert.Equal(ReplayResult.Succeeded, outcome.Result);
        Assert.Equal(2, (await ViewsAsync(app.Services)).Count);
        Assert.Equal(0, await PreservedCountAsync(app.Services));
    }

    [Fact]
    public async Task A_copy_left_by_a_dead_replay_is_restored_when_the_next_host_starts()
    {
        using var app = await BuildAsync("poc_restore_abandoned", restore: true);
        await SeedAsync(app.Services, count: 2);
        await ReplayAsync(app.Services);
        var before = await SnapshotAsync(app.Services);
        await PreserveAsync(app.Services, Guid.NewGuid());
        await ExecuteReadSqlAsync(app.Services, "DELETE FROM poc_counter_view");

        Assert.Equal(AbandonedPreservation.Restored, await RestoreAbandonedAsync(app.Services));

        Assert.Equal(before, await SnapshotAsync(app.Services));
        Assert.Equal(0, await PreservedCountAsync(app.Services));
    }

    [Fact]
    public async Task Two_hosts_starting_over_one_abandoned_copy_restore_it_once()
    {
        using var app = await BuildAsync("poc_restore_twice", restore: true);
        await SeedAsync(app.Services, count: 2);
        await ReplayAsync(app.Services);
        await PreserveAsync(app.Services, Guid.NewGuid());

        var restored = await Task.WhenAll(RestoreAbandonedAsync(app.Services), RestoreAbandonedAsync(app.Services));

        Assert.Single(restored, found => found == AbandonedPreservation.Restored);
    }

    [Fact]
    public async Task A_replay_requested_over_an_abandoned_copy_keeps_that_copy()
    {
        using var app = await BuildAsync("poc_restore_kept", restore: true);
        await SeedAsync(app.Services, count: 2);
        await ReplayAsync(app.Services);
        var before = await SnapshotAsync(app.Services);
        await PreserveAsync(app.Services, Guid.NewGuid());
        await ExecuteReadSqlAsync(app.Services,
            "INSERT INTO poc_counter_view (stream_id, value, last_version, applied_by_tenant, applications, applied_at) " +
            "VALUES (gen_random_uuid(), 99, 1, gen_random_uuid(), 1, now())");

        var second = Guid.NewGuid();
        await PreserveAsync(app.Services, second);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IReadModelPreservation>().RestoreAsync(second));

        Assert.Equal(before, await SnapshotAsync(app.Services));
    }

    [Fact]
    public async Task A_foreign_key_from_outside_the_set_fails_the_replay_before_anything_is_emptied()
    {
        using var app = await BuildAsync("poc_restore_outside", restore: true);
        await SeedAsync(app.Services, count: 2);
        await ReplayAsync(app.Services);
        var before = await SnapshotAsync(app.Services);
        await ExecuteReadSqlAsync(app.Services,
            "CREATE TABLE poc_outside_reference (id uuid PRIMARY KEY REFERENCES poc_counter_view (stream_id))");

        var outcome = await ReplayAsync(app.Services);

        Assert.Equal(ReplayResult.Failed, outcome.Result);
        Assert.Contains("poc_outside_reference", outcome.ErrorMessage);
        Assert.Equal(before, await SnapshotAsync(app.Services));
    }

    [Fact]
    public async Task A_reference_to_a_table_the_replay_does_not_empty_does_not_block_the_preservation()
    {
        using var app = await BuildAsync("poc_restore_lookup", restore: true, prepare: async services =>
        {
            await ExecuteReadSqlAsync(services, "CREATE TABLE poc_lookup (id integer PRIMARY KEY)");
            await ExecuteReadSqlAsync(services, "INSERT INTO poc_lookup VALUES (1)");
            await ExecuteReadSqlAsync(services, "ALTER TABLE poc_counter_totals ADD COLUMN lookup_id integer REFERENCES poc_lookup (id)");
        });
        await SeedAsync(app.Services, count: 1);

        var outcome = await ReplayAsync(app.Services);

        Assert.Equal(ReplayResult.Succeeded, outcome.Result);
    }

    [Fact]
    public async Task A_copy_left_by_a_replay_that_succeeded_is_not_reused_for_the_next()
    {
        using var app = await BuildAsync("poc_restore_stale", restore: true);
        var streams = await SeedAsync(app.Services, count: 2);
        await ReplayAsync(app.Services);
        var succeeded = await ReplayAsync(app.Services);
        await PreserveAsync(app.Services, succeeded.RequestId);
        await ExecuteReadSqlAsync(app.Services, "UPDATE poc_counter_view SET value = value + 100");
        var current = await SnapshotAsync(app.Services);

        app.Services.GetRequiredService<ProjectionProbeControl>().Poisoned.TryAdd(streams[^1], 0);
        var failed = await ReplayAsync(app.Services);

        Assert.True(failed.ReadModelsRestored);
        Assert.Equal(current, await SnapshotAsync(app.Services));
    }

    [Fact]
    public async Task A_copy_a_running_replay_owns_is_left_alone()
    {
        using var app = await BuildAsync("poc_restore_owned", restore: true);
        await SeedAsync(app.Services, count: 1);
        await ReplayAsync(app.Services);
        var running = Guid.NewGuid();
        var replay = app.Services.GetRequiredService<IProjectionReplayState>();
        Assert.True(replay.TryActivate(running));
        await PreserveAsync(app.Services, running);

        Assert.Equal(AbandonedPreservation.StillOwned, await RestoreAbandonedAsync(app.Services));

        replay.Complete(new ReplayCompletion(running, ReplayResult.Interrupted, 0));
        Assert.Equal(AbandonedPreservation.Restored, await RestoreAbandonedAsync(app.Services));
    }

    [Fact]
    public async Task A_restore_after_a_column_change_refuses_and_keeps_the_copy()
    {
        using var app = await BuildAsync("poc_restore_columns", restore: true);
        await SeedAsync(app.Services, count: 1);
        await ReplayAsync(app.Services);
        var replayId = Guid.NewGuid();
        await PreserveAsync(app.Services, replayId);
        await ExecuteReadSqlAsync(app.Services, "ALTER TABLE poc_counter_totals ADD COLUMN added_later integer");

        await using var scope = app.Services.CreateAsyncScope();
        var preservation = scope.ServiceProvider.GetRequiredService<IReadModelPreservation>();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => preservation.RestoreAsync(replayId));

        Assert.Contains("poc_counter_totals", refused.Message);
        Assert.True(await PreservedCountAsync(app.Services) > 0);
    }

    [Fact]
    public async Task Identity_sequences_continue_above_the_restored_rows()
    {
        using var app = await BuildAsync("poc_restore_identity", restore: true, prepare: async services =>
            await ExecuteReadSqlAsync(services,
                "CREATE TABLE poc_identity_probe (id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, label text NOT NULL)"));
        await ExecuteReadSqlAsync(app.Services, "INSERT INTO poc_identity_probe (label) VALUES ('a'), ('b'), ('c')");
        var replayId = Guid.NewGuid();
        await PreserveAsync(app.Services, replayId);
        await ExecuteReadSqlAsync(app.Services, "TRUNCATE poc_identity_probe RESTART IDENTITY");

        await using (var scope = app.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IReadModelPreservation>().RestoreAsync(replayId));
        }

        await ExecuteReadSqlAsync(app.Services, "INSERT INTO poc_identity_probe (label) VALUES ('d')");
        Assert.Equal(4, await ScalarReadSqlAsync<int>(app.Services, "SELECT id FROM poc_identity_probe WHERE label = 'd'"));
    }

    private async Task<IHost> BuildAsync(string store, bool restore, Func<IServiceProvider, Task>? prepare = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor($"{store}_write"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.AddEventProjectionServices();
        builder.Services
            .AddOutboxDispatcher()
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(postgres.ConnectionStringFor($"{store}_read"))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<CounterIncremented>()
            .AddScoped<IProjection, CounterViewProjection>()
            .AddScoped<CounterViewProjection>()
            .AddSingleton(new ProjectionProbeControl())
            .AddScoped<IProjectionViewTruncator, TruncatingAllProbeViews>();
        if (restore)
        {
            builder.Services.AddReadModelRestore<PocReadDbContext>(options => options.AdditionalTables.Add("poc_identity_probe"));
        }

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
            }

            await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await read.Database.EnsureCreatedAsync();
        }

        if (prepare is not null)
        {
            await prepare(app.Services);
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task<List<Guid>> SeedAsync(IServiceProvider services, int count)
    {
        var tenantId = Guid.NewGuid();
        var streams = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var streamId = Guid.CreateVersion7();
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
            var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
            await events.CreateAsync<Counter>(streamId, new CounterCreated(streamId));
            await events.AppendAsync<Counter>(streamId, new CounterIncremented(streamId, i + 1));
            await events.SaveChangesAsync();
            streams.Add(streamId);
        }

        return streams;
    }

    private static async Task<ReplayOutcome> ReplayAsync(IServiceProvider services)
    {
        var replay = services.GetRequiredService<IProjectionReplayState>();
        var requestId = Guid.CreateVersion7();
        replay.RequestReplay(requestId);

        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (replay.GetProgress().LastReplay is { } outcome && outcome.RequestId == requestId)
            {
                return outcome;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Replay {requestId} did not end within {Timeout}.");
    }

    private static async Task SeedFrameworkTablesAsync(IServiceProvider services)
    {
        await ExecuteReadSqlAsync(services,
            "INSERT INTO projection_checkpoint (projection, partition, reader, position) VALUES ('CounterViewProjection', 0, 'probe', 42)");
        await ExecuteReadSqlAsync(services,
            "INSERT INTO projection_forgotten_tenant (projection, tenant_id) VALUES ('CounterViewProjection', gen_random_uuid())");
    }

    private static async Task<string> SnapshotAsync(IServiceProvider services) =>
        await ScalarReadSqlAsync<string>(services,
            """
            SELECT concat_ws(' | ',
                (SELECT coalesce(string_agg(concat_ws(',', stream_id, value, last_version, applications, applied_at), ';' ORDER BY stream_id), '') FROM poc_counter_view),
                (SELECT coalesce(string_agg(concat_ws(',', projection, partition, reader, position), ';' ORDER BY projection, partition), '') FROM projection_checkpoint),
                (SELECT coalesce(string_agg(concat_ws(',', projection, tenant_id), ';' ORDER BY tenant_id), '') FROM projection_forgotten_tenant))
            """);

    private static async Task<List<CounterView>> ViewsAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
        return await context.CounterViews.AsNoTracking().ToListAsync();
    }

    private static async Task PreserveAsync(IServiceProvider services, Guid replayId)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IReadModelPreservation>().PreserveAsync(replayId);
    }

    private static async Task<AbandonedPreservation> RestoreAbandonedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IReadModelPreservation>().RestoreAbandonedAsync();
    }

    private static async Task<long> PreservedCountAsync(IServiceProvider services) =>
        await ScalarReadSqlAsync<long>(services,
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'stratara_replay' AND tablename NOT IN ('preservation', 'preserved_table')");

    private static async Task ExecuteReadSqlAsync(IServiceProvider services, string sql)
    {
        await using var scope = services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task<T> ScalarReadSqlAsync<T>(IServiceProvider services, string sql)
    {
        await using var scope = services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() is T value ? value : throw new InvalidOperationException($"'{sql}' returned no {typeof(T).Name}.");
    }

    /// <summary>Empties the probe's read models, as a consumer's truncator empties its own.</summary>
    public sealed class TruncatingAllProbeViews(IDbContextFactory<PocReadDbContext> contextFactory) : IProjectionViewTruncator
    {
        public async Task TruncateAllAsync(CancellationToken cancellationToken = default)
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await context.Database.ExecuteSqlRawAsync("TRUNCATE poc_counter_view, poc_counter_audit, poc_counter_totals", cancellationToken);
        }
    }
}
