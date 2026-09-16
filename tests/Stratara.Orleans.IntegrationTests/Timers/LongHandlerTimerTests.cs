using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.Timers;
using Stratara.Abstractions.Timers;

namespace Stratara.Orleans.IntegrationTests.Timers;

/// <summary>
/// A timer whose handler runs longer than the retry period and than the reminder call's response timeout: the
/// runtime gives up on the call and delivers the next tick while the handler still runs, and that tick does not
/// start the handler again. The response timeout is shortened so the case happens within seconds rather than
/// after the default thirty.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class LongHandlerTimerTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const string OrleansDatabase = "poc_orleans";
    private const int SiloPort = 11301;
    private const int GatewayPort = 30191;
    private static readonly TimeSpan DueIn = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HandlerDuration = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_handler_that_outlasts_the_reminder_call_runs_once()
    {
        var host = new SlowTimerHost(HandlerDuration);
        using var app = await StartAsync(host);
        var timers = app.Services.GetRequiredService<IDurableTimers>();
        var owner = $"slow-{Guid.NewGuid():N}";
        host.AddOwner(owner);

        await timers.RegisterAsync(new TimerRegistration(owner, "expire", DateTimeOffset.UtcNow + DueIn));

        var deadline = DateTimeOffset.UtcNow + SettleTimeout;
        while (DateTimeOffset.UtcNow < deadline && (host.Completed(owner) == 0 || (await timers.ListAsync(owner)).Count > 0))
        {
            await Task.Delay(250);
        }

        await Task.Delay(RetryPeriod * 3);

        Assert.Empty(await timers.ListAsync(owner));
        Assert.Equal(1, host.Started(owner));
        Assert.Equal(1, host.Completed(owner));
        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(SlowTimerHost timerHost)
    {
        var orleansConnectionString = postgres.ConnectionStringFor(OrleansDatabase);
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, SiloPort, GatewayPort)
            .Configure<SiloMessagingOptions>(options => options.ResponseTimeout = ResponseTimeout));
        builder.Services
            .AddStrataraDurableTimers(options => options.RetryPeriod = RetryPeriod)
            .AddSingleton<ITimerOwners>(timerHost)
            .AddSingleton<ITimerHandler>(timerHost);

        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    /// <summary>A timer host whose handler takes a fixed time and counts how often it was started and completed.</summary>
    private sealed class SlowTimerHost(TimeSpan duration) : ITimerOwners, ITimerHandler
    {
        private readonly ConcurrentDictionary<string, byte> _owners = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _started = new();
        private readonly ConcurrentQueue<string> _completed = new();

        public void AddOwner(string ownerId) => _owners[ownerId] = 0;

        public Task<bool> ExistsAsync(string ownerId, CancellationToken cancellationToken) =>
            Task.FromResult(_owners.ContainsKey(ownerId));

        public async Task OnDueAsync(TimerDue due, CancellationToken cancellationToken)
        {
            _started.Enqueue(due.OwnerId);
            await Task.Delay(duration, cancellationToken);
            _completed.Enqueue(due.OwnerId);
        }

        public int Started(string ownerId) => _started.Count(id => id == ownerId);

        public int Completed(string ownerId) => _completed.Count(id => id == ownerId);
    }
}
