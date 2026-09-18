using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Hosting;
using Stratara.Orleans.IntegrationTests.Timers;
using Stratara.Orleans.Projections;
using Stratara.Orleans.Timers;
using Stratara.Abstractions.Timers;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.EntityFrameworkCore.Projections;

using Stratara.Orleans.Sagas;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// Task 5.5: the shipped reset, run from a stopped host, leaves no reminder or membership row of the host's
/// deployment, no directory key and no checkpoint of a consumer the host registers, and a host started afterwards
/// fires nothing for the timers that existed before. A checkpoint of a consumer the host does not register stays
/// with its position; the checkpoint the host's sagas shared before 4.2.0 is removed with the host's own.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ResetTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const string OrleansDatabase = "poc_orleans";
    private const string ReadDatabase = "poc_reset_read";
    private const int SiloPort = 11241;

    /// <summary>The service and cluster id <see cref="PocSilo"/> gives a silo on <see cref="SiloPort"/>.</summary>
    private static readonly string Deployment = $"{PocSilo.ClusterId}-{SiloPort}";

    private const string HostConsumer = "probe";

    [Fact]
    public async Task One_reset_clears_timers_membership_directory_and_checkpoints()
    {
        var orleansConnectionString = postgres.ConnectionStringFor(OrleansDatabase);
        var readConnectionString = postgres.ConnectionStringFor(ReadDatabase);
        var timerHost = new RecordingTimerHost();
        var owner = $"reset-{Guid.NewGuid():N}";
        var foreignConsumer = $"another-deployment-{Guid.NewGuid():N}";

        ExecutionModelResetReport report;
        using (var app = await StartAsync(timerHost, orleansConnectionString, readConnectionString, gatewayPort: 30130))
        {
            timerHost.AddOwner(owner);
            await app.Services.GetRequiredService<IDurableTimers>().RegisterAsync(new TimerRegistration(owner, "expire", DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8)));
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var checkpoints = scope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>();
                await checkpoints.SetAsync(HostConsumer, 0, "probe-reader", 42);
                await checkpoints.SetAsync(HostConsumer, 1, "probe-reader", 42);
                await checkpoints.SetAsync(foreignConsumer, 0, "probe-reader", 42);
                await checkpoints.SetAsync(SagaGrain.ConsumerName, 0, "probe-reader", 42);
            }

            await app.StopAsync();
            Assert.True(await PocReset.CountAsync(orleansConnectionString, "orleansreminderstable", "serviceid", Deployment) > 0, "the host's reminders were gone before the reset — nothing to reset");

            await using var resetScope = app.Services.CreateAsyncScope();
            report = await resetScope.ServiceProvider.GetRequiredService<IExecutionModelReset>().ResetAsync();
        }

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        Assert.True(report.Reminders > 0, $"the reset reported no reminder removed: {report}");
        Assert.Equal(3, report.Checkpoints);
        Assert.Equal(0, await PocReset.CountAsync(orleansConnectionString, "orleansreminderstable", "serviceid", Deployment));
        Assert.Equal(0, await PocReset.CountAsync(orleansConnectionString, "orleansmembershiptable", "deploymentid", Deployment));
        Assert.Equal(0, await PocReset.CountAsync(readConnectionString, "projection_checkpoint", "projection", HostConsumer));
        Assert.Equal(0, await PocReset.CountAsync(readConnectionString, "projection_checkpoint", "projection", SagaGrain.ConsumerName));
        Assert.Equal(1, await PocReset.CountAsync(readConnectionString, "projection_checkpoint", "projection", foreignConsumer));
        await using (var read = new ServiceCollection().AddDbContextFactory<PocReadDbContext>(options => options.UseSnakeCaseNamingConvention().UseNpgsql(readConnectionString)).BuildServiceProvider())
        {
            var store = new ProjectionCheckpointStore<PocReadDbContext>(read.GetRequiredService<IDbContextFactory<PocReadDbContext>>());
            Assert.Equal(42, await store.GetAsync(foreignConsumer, 0, "probe-reader"));
        }
        Assert.Equal(0, redis.CountKeys());

        using (var restarted = await StartAsync(timerHost, orleansConnectionString, readConnectionString, gatewayPort: 30130))
        {
            await Task.Delay(PocSilo.RefreshReminderListPeriod * 2 + TimeSpan.FromSeconds(4));
            Assert.Empty(await restarted.Services.GetRequiredService<IDurableTimers>().ListAsync(owner));
            Assert.Equal(0, timerHost.FiringsFor(owner));
            await restarted.StopAsync();
        }
    }

    /// <summary>
    /// Scenario <em>The runtime tables live in a schema</em>: the scripts ran under a schema of their own, the host
    /// names it, and the reset removes and counts the deployment's rows there — without a silo, from a composition
    /// that registers no store reader.
    /// </summary>
    [Fact]
    public async Task A_reset_that_names_the_schema_clears_the_tables_there()
    {
        const string schema = "orleans_alt";
        const string deployment = "reset-in-a-schema";
        var runtime = postgres.ConnectionStringFor("poc_reset_schema");
        await PostgresTimerHostSchema.EnsureDatabaseAsync(runtime);
        await PocReset.CreateRuntimeTablesInSchemaAsync(runtime, schema);
        await PocReset.SeedDeploymentAsync(runtime, schema, deployment, reminders: 2, membershipRows: 1);
        var read = postgres.ConnectionStringFor(ReadDatabase);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(read);

        await using var services = ResetOnlyServices(runtime, read, deployment, schema);
        await using var scope = services.CreateAsyncScope();
        var report = await scope.ServiceProvider.GetRequiredService<IExecutionModelReset>().ResetAsync();

        Assert.Equal(2, report.Reminders);
        Assert.Equal(1, report.MembershipRows);
        Assert.Equal(0, report.Checkpoints);
        Assert.Equal(0, await PocReset.CountAsync(runtime, $"{schema}.orleansreminderstable", "serviceid", deployment));
        Assert.Equal(0, await PocReset.CountAsync(runtime, $"{schema}.orleansmembershiptable", "deploymentid", deployment));
        Assert.Equal(0, await PocReset.CountAsync(runtime, $"{schema}.orleansmembershipversiontable", "deploymentid", deployment));
    }

    /// <summary>Scenario <em>A runtime table is absent</em>: a reset against a database without the tables fails naming the first one.</summary>
    [Fact]
    public async Task A_reset_against_absent_tables_fails_naming_the_table()
    {
        var runtime = postgres.ConnectionStringFor("poc_reset_absent");
        await PostgresTimerHostSchema.EnsureDatabaseAsync(runtime);
        var read = postgres.ConnectionStringFor(ReadDatabase);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(read);

        await using var services = ResetOnlyServices(runtime, read, "reset-absent", "public");
        await using var scope = services.CreateAsyncScope();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IExecutionModelReset>().ResetAsync());

        Assert.Contains("public.orleansreminderstable", failure.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider ResetOnlyServices(string runtime, string read, string deployment, string schema) =>
        new ServiceCollection()
            .Configure<global::Orleans.Configuration.ClusterOptions>(options =>
            {
                options.ClusterId = deployment;
                options.ServiceId = deployment;
            })
            .AddDbContextFactory<PocReadDbContext>(options => options.UseSnakeCaseNamingConvention().UseNpgsql(read))
            .AddStrataraExecutionModelReset<PocReadDbContext>(runtime, (_, _) => Task.FromResult(0L), schema)
            .BuildServiceProvider();

    private async Task<IHost> StartAsync(RecordingTimerHost timerHost, string orleansConnectionString, string readConnectionString, int gatewayPort)
    {
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(readConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, SiloPort, gatewayPort));
        builder.Services
            .AddStrataraDurableTimers(options => options.RetryPeriod = TimeSpan.FromSeconds(1))
            .AddSingleton(timerHost)
            .AddSingleton<ITimerOwners>(timerHost)
            .AddSingleton<ITimerHandler>(timerHost)
            .AddDbContextFactory<PocReadDbContext>(options => options.UseSnakeCaseNamingConvention().UseNpgsql(readConnectionString))
            .AddScoped<IProjectionCheckpointStore, ProjectionCheckpointStore<PocReadDbContext>>()
            .AddScoped<INudgeTarget, ProbeConsumer>()
            .AddStrataraExecutionModelReset<PocReadDbContext>(orleansConnectionString, (_, _) => PocReset.ClearDirectoryAsync(redis.ConnectionString));

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await read.Database.EnsureCreatedAsync();
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    /// <summary>A store reader the host registers under <see cref="HostConsumer"/>, without a grain behind it.</summary>
    private sealed class ProbeConsumer : INudgeTarget
    {
        public IReadOnlyList<string> ConsumerNames { get; } = [HostConsumer];

        public IReadOnlyList<string> SupersededConsumerNames { get; } = [SagaGrain.ConsumerName];

        public Task NudgeAsync(IGrainFactory grainFactory, int partition) => Task.CompletedTask;

        public Task EnsureRunningAsync(IGrainFactory grainFactory, int partition) => Task.CompletedTask;
    }
}
