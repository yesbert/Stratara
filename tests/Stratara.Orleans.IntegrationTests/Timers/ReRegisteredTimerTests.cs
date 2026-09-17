using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Timers;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.Timers;

namespace Stratara.Orleans.IntegrationTests.Timers;

/// <summary>
/// A timer's handler registers a timer for its own owner and purpose and returns: with the same due time the timer is
/// still registered afterwards and fires again; with a later due time only the later timer remains (scenarios <em>A
/// timer is re-registered from its own handler with the same due time</em>, <em>… with a later due time</em>). An owner
/// id of the longest length the timers accept renders to a grain id the PostgreSQL reminder table holds.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ReRegisteredTimerTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const string Purpose = "expire";
    private static readonly TimeSpan DueIn = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Later = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_timer_re_registered_with_the_same_due_time_is_kept_and_fires_again()
    {
        var host = new ReRegisteringHost(delay: TimeSpan.Zero);
        using var app = await StartAsync(host, siloPort: 11244, gatewayPort: 30133);
        var timers = app.Services.GetRequiredService<IDurableTimers>();
        var owner = $"renew-{Guid.NewGuid():N}";
        host.AddOwner(owner);

        await timers.RegisterAsync(new TimerRegistration(owner, Purpose, DateTimeOffset.UtcNow + DueIn));

        Assert.True(await WaitUntilAsync(() => host.Fired(owner) >= 2), $"the re-registered timer did not fire again; fired {host.Fired(owner)}, listed {(await timers.ListAsync(owner)).Count}");
        await app.StopAsync();
    }

    [Fact]
    public async Task A_timer_re_registered_with_a_later_due_time_leaves_only_the_later_timer()
    {
        var host = new ReRegisteringHost(delay: Later);
        using var app = await StartAsync(host, siloPort: 11246, gatewayPort: 30134);
        var timers = app.Services.GetRequiredService<IDurableTimers>();
        var owner = $"later-{Guid.NewGuid():N}";
        host.AddOwner(owner);
        var firstDue = DateTimeOffset.UtcNow + DueIn;

        await timers.RegisterAsync(new TimerRegistration(owner, Purpose, firstDue));

        Assert.True(await WaitUntilAsync(() => host.Fired(owner) >= 1), "the timer never fired");
        await Task.Delay(RetryPeriod);
        var listed = await timers.ListAsync(owner);
        var single = Assert.Single(listed);
        Assert.True(single.DueAt > firstDue, $"the remaining timer is due {single.DueAt:O}, not after {firstDue:O}");
        Assert.True(await WaitUntilAsync(() => host.Fired(owner) >= 2), "the later timer did not fire");
        await app.StopAsync();
    }

    [Fact]
    public async Task An_owner_id_of_the_longest_length_is_held_by_the_reminder_table()
    {
        var host = new ReRegisteringHost(delay: TimeSpan.Zero);
        using var app = await StartAsync(host, siloPort: 11247, gatewayPort: 30136);
        var timers = app.Services.GetRequiredService<IDurableTimers>();
        var owner = string.Concat(Guid.NewGuid().ToString("N"), new string('o', ReminderName.MaxOwnerIdLength - 32));
        host.AddOwner(owner);

        var grainId = app.Services.GetRequiredService<IGrainFactory>().GetGrain<ITimerOwnerGrain>(owner).GetGrainId().ToString();
        Assert.Equal(ReminderName.ReminderStoreGrainIdLength, grainId.Length);

        await timers.RegisterAsync(new TimerRegistration(owner, Purpose, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)));
        Assert.Single(await timers.ListAsync(owner));
        await timers.CancelAllAsync(owner);
        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(ReRegisteringHost timerHost, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.Services
            .AddStrataraDurableTimers(options => options.RetryPeriod = RetryPeriod)
            .AddSingleton<ITimerOwners>(timerHost)
            .AddSingleton<ITimerHandler>(timerHost);

        var app = builder.Build();
        timerHost.Timers = app.Services.GetRequiredService<IDurableTimers>();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + SettleTimeout;
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

    /// <summary>A timer host whose handler, on the first firing of an owner, registers the owner's timer again — at the due time it fired for plus <c>delay</c>.</summary>
    private sealed class ReRegisteringHost(TimeSpan delay) : ITimerOwners, ITimerHandler
    {
        private readonly ConcurrentDictionary<string, byte> _owners = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _fired = new(StringComparer.Ordinal);

        public IDurableTimers? Timers { get; set; }

        public void AddOwner(string ownerId) => _owners[ownerId] = 0;

        public int Fired(string ownerId) => _fired.GetValueOrDefault(ownerId);

        public Task<bool> ExistsAsync(string ownerId, CancellationToken cancellationToken) => Task.FromResult(_owners.ContainsKey(ownerId));

        public async Task OnDueAsync(TimerDue due, CancellationToken cancellationToken)
        {
            if (_fired.AddOrUpdate(due.OwnerId, 1, static (_, count) => count + 1) == 1 && Timers is { } timers)
            {
                await timers.RegisterAsync(new TimerRegistration(due.OwnerId, due.Purpose, due.DueAt + delay), cancellationToken);
            }
        }
    }
}
