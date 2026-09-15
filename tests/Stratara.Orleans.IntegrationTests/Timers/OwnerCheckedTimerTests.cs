using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.Timers;
using Stratara.Abstractions.Timers;

namespace Stratara.Orleans.IntegrationTests.Timers;

/// <summary>
/// T4 of the expectations: a timer whose owner is removed while it is due fires nothing and leaves
/// no timer behind. Twenty owners, registered together, removed together before they are due. The
/// positive control beside it shows the same timers firing exactly once when the owner stays.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class OwnerCheckedTimerTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const string OrleansDatabase = "poc_orleans";
    private const int Owners = 20;
    private static readonly TimeSpan DueIn = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Owner_removed_while_due_fires_nothing_and_leaves_no_timer()
    {
        var host = new RecordingTimerHost();
        using var app = await StartAsync(host, siloPort: 11131, gatewayPort: 30020);
        var timers = app.Services.GetRequiredService<IDurableTimers>();
        var owners = Enumerable.Range(0, Owners).Select(i => $"removed-{Guid.NewGuid():N}-{i}").ToList();

        foreach (var owner in owners)
        {
            host.AddOwner(owner);
            await timers.RegisterAsync(new TimerRegistration(owner, "expire", DateTimeOffset.UtcNow + DueIn));
        }

        await Task.Delay(DueIn / 4);
        foreach (var owner in owners)
        {
            host.RemoveOwner(owner);
        }

        await WaitUntilNoTimersAsync(timers, owners);

        Assert.All(owners, owner => Assert.Equal(0, host.FiringsFor(owner)));
        Assert.Empty(host.Fired);
        await app.StopAsync();
    }

    [Fact]
    public async Task Kept_owner_fires_exactly_once_and_leaves_no_timer()
    {
        var host = new RecordingTimerHost();
        using var app = await StartAsync(host, siloPort: 11141, gatewayPort: 30030);
        var timers = app.Services.GetRequiredService<IDurableTimers>();
        var owners = Enumerable.Range(0, Owners).Select(i => $"kept-{Guid.NewGuid():N}-{i}").ToList();

        foreach (var owner in owners)
        {
            host.AddOwner(owner);
            await timers.RegisterAsync(new TimerRegistration(owner, "expire", DateTimeOffset.UtcNow + DueIn));
        }

        await WaitUntilNoTimersAsync(timers, owners);
        await Task.Delay(RetryPeriod * 2);

        Assert.All(owners, owner => Assert.Equal(1, host.FiringsFor(owner)));
        Assert.All(host.Fired, due => Assert.True(due.FiredAt >= due.DueAt, $"{due.OwnerId} fired {due.DueAt - due.FiredAt} early"));
        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(RecordingTimerHost timerHost, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor(OrleansDatabase);
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.Services
            .AddStrataraDurableTimers(options => options.RetryPeriod = RetryPeriod)
            .AddSingleton(timerHost)
            .AddSingleton<ITimerOwners>(timerHost)
            .AddSingleton<ITimerHandler>(timerHost);

        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task WaitUntilNoTimersAsync(IDurableTimers timers, IReadOnlyList<string> owners)
    {
        var deadline = DateTimeOffset.UtcNow + SettleTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var remaining = 0;
            foreach (var owner in owners)
            {
                remaining += (await timers.ListAsync(owner)).Count;
            }

            if (remaining == 0)
            {
                return;
            }

            await Task.Delay(250);
        }

        var leftovers = new List<string>();
        foreach (var owner in owners)
        {
            leftovers.AddRange((await timers.ListAsync(owner)).Select(t => $"{t.OwnerId}/{t.Purpose}"));
        }

        Assert.Fail($"Timers still registered after {SettleTimeout}: {string.Join(", ", leftovers)}");
    }
}
