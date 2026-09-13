using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Timers;
using Stratara.Orleans.Projections;
using Stratara.Orleans.Timers;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// Task 11.1: after the reset no reminder, membership row, directory key or checkpoint remains, and a
/// host started afterwards fires nothing for the timers that existed before.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ResetTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const string OrleansDatabase = "poc_orleans";
    private const string ReadDatabase = "poc_reset_read";

    [Fact]
    public async Task One_reset_clears_timers_membership_directory_and_checkpoints()
    {
        var orleansConnectionString = postgres.ConnectionStringFor(OrleansDatabase);
        var readConnectionString = postgres.ConnectionStringFor(ReadDatabase);
        var timerHost = new RecordingTimerHost();
        var owner = $"reset-{Guid.NewGuid():N}";

        using (var app = await StartAsync(timerHost, orleansConnectionString, readConnectionString, siloPort: 11241, gatewayPort: 30130))
        {
            timerHost.AddOwner(owner);
            await app.Services.GetRequiredService<IDurableTimers>().RegisterAsync(new TimerRegistration(owner, "expire", DateTimeOffset.UtcNow + TimeSpan.FromSeconds(8)));
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IProjectionCheckpointStore>().SetAsync("probe", 0, "probe-reader", 42);
            await app.StopAsync();
        }

        Assert.True(await PocReset.CountAsync(orleansConnectionString, "orleansreminderstable") > 0, "the reminder table was empty before the reset — nothing to reset");

        var report = await PocReset.RunAsync(orleansConnectionString, redis.ConnectionString, readConnectionString);
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());

        Assert.Equal(0, await PocReset.CountAsync(orleansConnectionString, "orleansreminderstable"));
        Assert.Equal(0, await PocReset.CountAsync(orleansConnectionString, "orleansmembershiptable"));
        Assert.Equal(0, await PocReset.CountAsync(readConnectionString, "projection_checkpoint"));
        Assert.Equal(0, redis.CountKeys());

        using (var restarted = await StartAsync(timerHost, orleansConnectionString, readConnectionString, siloPort: 11241, gatewayPort: 30130))
        {
            await Task.Delay(PocSilo.RefreshReminderListPeriod * 2 + TimeSpan.FromSeconds(4));
            Assert.Empty(await restarted.Services.GetRequiredService<IDurableTimers>().ListAsync(owner));
            Assert.Equal(0, timerHost.FiringsFor(owner));
            await restarted.StopAsync();
        }
    }

    private async Task<IHost> StartAsync(RecordingTimerHost timerHost, string orleansConnectionString, string readConnectionString, int siloPort, int gatewayPort)
    {
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);
        await PostgresTimerHostSchema.EnsureDatabaseAsync(readConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.Services
            .AddStrataraDurableTimers(options => options.RetryPeriod = TimeSpan.FromSeconds(1))
            .AddSingleton(timerHost)
            .AddSingleton<ITimerOwners>(timerHost)
            .AddSingleton<ITimerHandler>(timerHost)
            .AddDbContextFactory<PocReadDbContext>(options => options.UseSnakeCaseNamingConvention().UseNpgsql(readConnectionString))
            .AddScoped<IProjectionCheckpointStore, ProjectionCheckpointStore<PocReadDbContext>>();

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
