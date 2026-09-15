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

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// Task 5.5: the shipped reset, run from a stopped host, leaves no reminder or membership row of the host's
/// deployment, no directory key and no checkpoint, and a host started afterwards fires nothing for the timers
/// that existed before.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ResetTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const string OrleansDatabase = "poc_orleans";
    private const string ReadDatabase = "poc_reset_read";
    private const int SiloPort = 11241;

    /// <summary>The service and cluster id <see cref="PocSilo"/> gives a silo on <see cref="SiloPort"/>.</summary>
    private static readonly string Deployment = $"{PocSilo.ClusterId}-{SiloPort}";

    [Fact]
    public async Task One_reset_clears_timers_membership_directory_and_checkpoints()
    {
        var orleansConnectionString = postgres.ConnectionStringFor(OrleansDatabase);
        var readConnectionString = postgres.ConnectionStringFor(ReadDatabase);
        var timerHost = new RecordingTimerHost();
        var owner = $"reset-{Guid.NewGuid():N}";

        ExecutionModelResetReport report;
        using (var app = await StartAsync(timerHost, orleansConnectionString, readConnectionString, gatewayPort: 30130))
        {
            timerHost.AddOwner(owner);
            await app.Services.GetRequiredService<IDurableTimers>().RegisterAsync(new TimerRegistration(owner, "expire", DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8)));
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>().SetAsync("probe", 0, "probe-reader", 42);
            }

            await app.StopAsync();
            Assert.True(await PocReset.CountAsync(orleansConnectionString, "orleansreminderstable", "serviceid", Deployment) > 0, "the host's reminders were gone before the reset — nothing to reset");

            await using var resetScope = app.Services.CreateAsyncScope();
            report = await resetScope.ServiceProvider.GetRequiredService<IExecutionModelReset>().ResetAsync();
        }

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        Assert.True(report.Reminders > 0 && report.Checkpoints > 0, $"the reset reported nothing removed: {report}");
        Assert.Equal(0, await PocReset.CountAsync(orleansConnectionString, "orleansreminderstable", "serviceid", Deployment));
        Assert.Equal(0, await PocReset.CountAsync(orleansConnectionString, "orleansmembershiptable", "deploymentid", Deployment));
        Assert.Equal(0, await PocReset.CountAsync(readConnectionString, "projection_checkpoint"));
        Assert.Equal(0, redis.CountKeys());

        using (var restarted = await StartAsync(timerHost, orleansConnectionString, readConnectionString, gatewayPort: 30130))
        {
            await Task.Delay(PocSilo.RefreshReminderListPeriod * 2 + TimeSpan.FromSeconds(4));
            Assert.Empty(await restarted.Services.GetRequiredService<IDurableTimers>().ListAsync(owner));
            Assert.Equal(0, timerHost.FiringsFor(owner));
            await restarted.StopAsync();
        }
    }

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
}
