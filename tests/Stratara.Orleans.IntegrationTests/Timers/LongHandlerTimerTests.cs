using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.Timers;
using Stratara.Abstractions.Timers;

namespace Stratara.Orleans.IntegrationTests.Timers;

/// <summary>
/// A timer whose handler runs longer than the retry period and than the reminder call's response timeout: the tick is
/// answered at once, the next tick arrives while the handler still runs and does not start it again, and the silo logs
/// no undelivered tick for the owner (scenario <em>A timer's handler runs longer than the response timeout</em>). The
/// response timeout is shortened so the case happens within seconds rather than after the default thirty.
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
        var logs = new CapturedLogs();
        using var app = await StartAsync(host, logs);
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
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains(owner, StringComparison.Ordinal)
            && (entry.Contains("Could not deliver reminder tick", StringComparison.Ordinal) || entry.Contains("Response did not arrive on time", StringComparison.Ordinal)));
        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(SlowTimerHost timerHost, CapturedLogs logs)
    {
        var orleansConnectionString = postgres.ConnectionStringFor(OrleansDatabase);
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Logging.AddProvider(logs);
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

    /// <summary>Every log line the host writes, rendered with its exception.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(Entries);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }
}
