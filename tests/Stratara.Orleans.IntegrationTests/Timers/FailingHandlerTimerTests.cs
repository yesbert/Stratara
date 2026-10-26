using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Stratara.Abstractions.Timers;
using Stratara.Diagnostics;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.Timers;

namespace Stratara.Orleans.IntegrationTests.Timers;

/// <summary>
/// Scenario <em>A timer's handler fails</em>: a handler that throws on its first attempt is logged once by the framework,
/// at error level with the owner and the purpose; the timer stays registered, fires again a retry period later, and is
/// unregistered once the second attempt has returned.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class FailingHandlerTimerTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const string OrleansDatabase = "poc_orleans";
    private const string Purpose = "expire";
    private const int SiloPort = 11541;
    private const int GatewayPort = 30541;
    private static readonly TimeSpan DueIn = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_handler_that_throws_is_logged_and_fires_again()
    {
        var host = new ThrowOnceTimerHost();
        var logs = new CapturedLogs();
        using var app = await StartAsync(host, logs);
        var timers = app.Services.GetRequiredService<IDurableTimers>();
        var owner = $"failing-{Guid.NewGuid():N}";
        host.AddOwner(owner);

        await timers.RegisterAsync(new TimerRegistration(owner, Purpose, DateTimeOffset.UtcNow + DueIn));

        var deadline = DateTimeOffset.UtcNow + SettleTimeout;
        while (DateTimeOffset.UtcNow < deadline && (host.Attempts(owner) < 2 || (await timers.ListAsync(owner)).Count > 0))
        {
            await Task.Delay(250);
        }

        await Task.Delay(RetryPeriod * 3);

        Assert.Equal(2, host.Attempts(owner));
        Assert.Empty(await timers.ListAsync(owner));
        var failure = Assert.Single(logs.Entries, entry => entry.EventId == LogEvents.Orleans.TimerHandlerFailed && entry.Message.Contains(owner, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, failure.Level);
        Assert.Contains(Purpose, failure.Message, StringComparison.Ordinal);
        Assert.IsType<InvalidOperationException>(failure.Exception);
        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(ThrowOnceTimerHost timerHost, CapturedLogs logs)
    {
        var orleansConnectionString = postgres.ConnectionStringFor(OrleansDatabase);
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Logging.AddProvider(logs);
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, SiloPort, GatewayPort)
            .Configure<SiloMessagingOptions>(options => options.ResponseTimeout = TimeSpan.FromSeconds(5)));
        builder.Services
            .AddStrataraDurableTimers(options => options.RetryPeriod = RetryPeriod)
            .AddSingleton<ITimerOwners>(timerHost)
            .AddSingleton<ITimerHandler>(timerHost);

        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    /// <summary>A timer host whose handler throws on an owner's first attempt and returns on every later one.</summary>
    private sealed class ThrowOnceTimerHost : ITimerOwners, ITimerHandler
    {
        private readonly ConcurrentDictionary<string, byte> _owners = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

        public void AddOwner(string ownerId) => _owners[ownerId] = 0;

        public Task<bool> ExistsAsync(string ownerId, CancellationToken cancellationToken) =>
            Task.FromResult(_owners.ContainsKey(ownerId));

        public Task OnDueAsync(TimerDue due, CancellationToken cancellationToken) =>
            _attempts.AddOrUpdate(due.OwnerId, 1, (_, attempts) => attempts + 1) == 1
                ? throw new InvalidOperationException("the first attempt fails")
                : Task.CompletedTask;

        public int Attempts(string ownerId) => _attempts.GetValueOrDefault(ownerId);
    }

    /// <summary>Every log entry the host writes, with its event id, level, rendered message and exception.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<(int EventId, LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(Entries);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<(int EventId, LogLevel Level, string Message, Exception? Exception)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((eventId.Id, logLevel, formatter(state, exception), exception));
        }
    }
}
