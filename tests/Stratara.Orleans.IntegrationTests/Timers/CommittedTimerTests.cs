using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.EventSourcing;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.Timers;
using Stratara.Abstractions.Timers;

namespace Stratara.Orleans.IntegrationTests.Timers;

/// <summary>
/// A timer whose handler's save committed its events but could not publish them counts as fired: firing it again would
/// record the handler's facts a second time.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class CommittedTimerTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const string OrleansDatabase = "poc_orleans";
    private const int SiloPort = 11381;
    private const int GatewayPort = 30381;
    private static readonly TimeSpan DueIn = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_handler_that_committed_but_could_not_publish_fires_once_and_is_unregistered()
    {
        var host = new CommittingTimerHost();
        using var app = await StartAsync(host);
        var timers = app.Services.GetRequiredService<IDurableTimers>();
        var owner = $"committed-{Guid.NewGuid():N}";
        host.AddOwner(owner);

        await timers.RegisterAsync(new TimerRegistration(owner, "expire", DateTimeOffset.UtcNow + DueIn));

        var deadline = DateTimeOffset.UtcNow + SettleTimeout;
        while (DateTimeOffset.UtcNow < deadline && (host.Started(owner) == 0 || (await timers.ListAsync(owner)).Count > 0))
        {
            await Task.Delay(250);
        }

        await Task.Delay(RetryPeriod * 3);

        Assert.Empty(await timers.ListAsync(owner));
        Assert.Equal(1, host.Started(owner));
        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(CommittingTimerHost timerHost)
    {
        var orleansConnectionString = postgres.ConnectionStringFor(OrleansDatabase);
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, SiloPort, GatewayPort));
        builder.Services
            .AddStrataraDurableTimers(options => options.RetryPeriod = RetryPeriod)
            .AddSingleton<ITimerOwners>(timerHost)
            .AddSingleton<ITimerHandler>(timerHost);

        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    /// <summary>A timer host whose handler behaves as one whose save committed but could not hand its bundle on.</summary>
    private sealed class CommittingTimerHost : ITimerOwners, ITimerHandler
    {
        private readonly ConcurrentDictionary<string, byte> _owners = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _started = new();

        public void AddOwner(string ownerId) => _owners[ownerId] = 0;

        public Task<bool> ExistsAsync(string ownerId, CancellationToken cancellationToken) =>
            Task.FromResult(_owners.ContainsKey(ownerId));

        public Task OnDueAsync(TimerDue due, CancellationToken cancellationToken)
        {
            _started.Enqueue(due.OwnerId);
            throw new CommittedEventsNotPublishedException([Guid.NewGuid()], 1, new InvalidOperationException("the outbox table is down"));
        }

        public int Started(string ownerId) => _started.Count(id => id == ownerId);
    }
}
