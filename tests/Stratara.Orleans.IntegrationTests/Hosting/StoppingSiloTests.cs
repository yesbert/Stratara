using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Abstractions.Timers;
using Stratara.Diagnostics;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Singleton;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// A silo is stopped while a handler on a grain path runs. A handler that waits on its token observes the
/// cancellation within the deactivation budget, and what it did not finish runs again elsewhere: the timer fires on
/// the remaining silo, the recorded command is resumed there, and the caller of a forwarded command is told the silo
/// stopped. A handler that ignores the token runs to its end before the silo stops (scenarios <em>A silo stops while a
/// timer's handler runs</em>, <em>… while a recorded command's handler runs</em>, <em>… while a forwarded command's
/// handler runs</em>, <em>A handler ignores the cancellation</em>).
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class StoppingSiloTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan ShortBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LongBudget = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartedTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TakeoverTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    public async Task A_timer_whose_handler_waits_on_its_token_observes_the_stop_and_fires_on_the_remaining_silo()
    {
        var control = new StopProbeControl();
        var cluster = $"stratara-poc-stop-timer-{Guid.NewGuid():N}";
        using var first = await StartSiloAsync(control, cluster, 11248, 30138, ShortBudget);
        var owner = $"stop-{Guid.NewGuid():N}";

        await first.Services.GetRequiredService<IDurableTimers>().RegisterAsync(new TimerRegistration(owner, "expire", DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1)));
        Assert.True(await WaitUntilAsync(() => control.Started(owner) == 1, StartedTimeout), "the timer's handler never started");

        using var second = await StartSiloAsync(control, cluster, 11249, 30139, ShortBudget);
        await StopAsync(first);

        Assert.Equal(1, control.Cancelled(owner));
        Assert.Contains(control.Logs.Entries, e => e.EventId == LogEvents.Orleans.HandlerStoppedWithSilo && e.Message.Contains(owner, StringComparison.Ordinal) && e.Message.Contains("expire", StringComparison.Ordinal));
        control.Block = false;
        Assert.True(await WaitUntilAsync(() => control.Completed(owner) == 1, TakeoverTimeout), "the timer did not fire on the remaining silo");
        Assert.True(await WaitUntilAsync(async () => (await second.Services.GetRequiredService<IDurableTimers>().ListAsync(owner)).Count == 0, StartedTimeout), "the timer stayed registered after its handler completed");
        await second.StopAsync();
    }

    [Fact]
    public async Task A_recorded_command_whose_handler_waits_on_its_token_observes_the_stop_and_completes_on_the_remaining_silo()
    {
        var control = new StopProbeControl();
        var cluster = $"stratara-poc-stop-intent-{Guid.NewGuid():N}";
        using var first = await StartSiloAsync(control, cluster, 11250, 30140, ShortBudget);
        var probe = Guid.NewGuid();

        await using (var scope = first.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            await scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(new BlockingProbe(Guid.NewGuid(), probe));
        }

        Assert.True(await WaitUntilAsync(() => control.Started(probe.ToString()) == 1, StartedTimeout), "the recorded command's handler never started");
        using var second = await StartSiloAsync(control, cluster, 11251, 30141, ShortBudget);
        await StopAsync(first);

        Assert.Equal(1, control.Cancelled(probe.ToString()));
        Assert.Contains(control.Logs.Entries, e => e.EventId == LogEvents.Orleans.HandlerStoppedWithSilo && e.Message.Contains(nameof(BlockingProbe), StringComparison.Ordinal) && e.Message.Contains("intent ", StringComparison.Ordinal));
        control.Block = false;
        Assert.True(await WaitUntilAsync(() => control.Completed(probe.ToString()) == 1, TakeoverTimeout), "the recorded command was not resumed on the remaining silo");
        Assert.Equal(2, control.Started(probe.ToString()));
        await second.StopAsync();
    }

    [Fact]
    public async Task The_caller_of_a_forwarded_command_whose_handler_waits_on_its_token_is_told_the_silo_stopped()
    {
        var control = new StopProbeControl();
        var cluster = $"stratara-poc-stop-forward-{Guid.NewGuid():N}";
        using var first = await StartSiloAsync(control, cluster, 11252, 30142, ShortBudget);
        var aggregate = Guid.NewGuid();
        control.Block = false;
        await SendAsync(first, new BlockingProbe(aggregate, Guid.NewGuid()));
        control.Block = true;

        using var second = await StartSiloAsync(control, cluster, 11253, 30143, ShortBudget);
        var probe = Guid.NewGuid();
        var call = SendAsync(second, new BlockingProbe(aggregate, probe));
        Assert.True(await WaitUntilAsync(() => control.Started(probe.ToString()) == 1, StartedTimeout), "the forwarded command's handler never started on the first silo");
        await StopAsync(first);

        var failed = await Assert.ThrowsAnyAsync<Exception>(() => call.WaitAsync(TakeoverTimeout));
        Assert.Equal(1, control.Cancelled(probe.ToString()));
        Assert.Contains("stopped", failed.Message, StringComparison.Ordinal);
        Assert.Contains(control.Logs.Entries, e => e.EventId == LogEvents.Orleans.HandlerStoppedWithSilo && e.Message.Contains(aggregate.ToString(), StringComparison.Ordinal));
        await second.StopAsync();
    }

    [Fact]
    public async Task A_handler_that_ignores_its_token_runs_to_its_end_before_the_silo_stops()
    {
        var control = new StopProbeControl();
        var cluster = $"stratara-poc-stop-ignore-{Guid.NewGuid():N}";
        using var silo = await StartSiloAsync(control, cluster, 11254, 30144, LongBudget);
        var probe = Guid.NewGuid();

        await using (var scope = silo.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
            await scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>().EnqueueCommandAsync(new IgnoringProbe(Guid.NewGuid(), probe, DelayMs: 4_000));
        }

        Assert.True(await WaitUntilAsync(() => control.Started(probe.ToString()) == 1, StartedTimeout), "the handler never started");
        await StopAsync(silo);

        Assert.Equal(1, control.Completed(probe.ToString()));
        Assert.Equal(0, control.Cancelled(probe.ToString()));
    }

    private static async Task SendAsync(IHost host, BlockingProbe command)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
        await scope.ServiceProvider.GetRequiredService<IMediator>().HandleAsync(command);
    }

    private static async Task StopAsync(IHost host)
    {
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await host.StopAsync(stopTimeout.Token);
    }

    private async Task<IHost> StartSiloAsync(StopProbeControl control, string clusterId, int siloPort, int gatewayPort, TimeSpan deactivationBudget)
    {
        var orleans = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleans);
        var store = postgres.ConnectionStringFor("poc_stopping_store");

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = store,
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleans, redis.ConnectionString, siloPort, gatewayPort, clusterId: clusterId)
            .Configure<GrainCollectionOptions>(options => options.DeactivationTimeout = deactivationBudget));
        builder.Logging.AddProvider(control.Logs);
        builder.AddBackendServices();
        builder.Services
            .AddSingleton(control)
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<BlockingProbe>, BlockingProbeHandler>()
            .AddScoped<ICommandHandler<IgnoringProbe>, IgnoringProbeHandler>()
            .AddAggregatesFromAssemblyContaining<IntentScenario>()
            .AddTrustedType<BlockingProbe>()
            .AddTrustedType<IgnoringProbe>()
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher(options => options.IntentGrace = TimeSpan.FromSeconds(2))
            .AddStrataraIntentStore<PocWriteDbContext>()
            .AddStrataraSingletonWork<OutboxDrainWork>(options => options.KeepAlivePeriod = TimeSpan.FromSeconds(5))
            .Configure<OutboxDrainOptions>(options => options.PollingInterval = TimeSpan.FromSeconds(1))
            .AddStrataraDurableTimers(options => options.RetryPeriod = TimeSpan.FromSeconds(1))
            .AddSingleton<ITimerOwners>(control)
            .AddSingleton<ITimerHandler>(control);

        var host = builder.Build();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await host.StartAsync(startTimeout.Token);
        return host;
    }

    private static Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout) => WaitUntilAsync(() => Task.FromResult(condition()), timeout);

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }
}

/// <summary>A command whose handler waits on its token while the control blocks, and completes otherwise.</summary>
public sealed record BlockingProbe(Guid AggregateId, Guid ProbeId) : ICommand, IAggregateScopedCommand;

/// <summary>A command whose handler waits a fixed time without looking at its token.</summary>
public sealed record IgnoringProbe(Guid AggregateId, Guid ProbeId, int DelayMs) : ICommand, IAggregateScopedCommand;

/// <summary>What the stop probes did, by probe id or timer owner, shared by every silo of a test; also the timer host whose handler blocks the same way.</summary>
public sealed class StopProbeControl : ITimerOwners, ITimerHandler
{
    private readonly ConcurrentDictionary<string, int> _started = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _cancelled = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _completed = new(StringComparer.Ordinal);
    private volatile bool _block = true;

    public CapturedLogs Logs { get; } = new();

    public bool Block
    {
        get => _block;
        set => _block = value;
    }

    public int Started(string id) => _started.GetValueOrDefault(id);

    public int Cancelled(string id) => _cancelled.GetValueOrDefault(id);

    public int Completed(string id) => _completed.GetValueOrDefault(id);

    public async Task RunAsync(string id, CancellationToken cancellationToken)
    {
        _started.AddOrUpdate(id, 1, static (_, count) => count + 1);
        if (Block)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _cancelled.AddOrUpdate(id, 1, static (_, count) => count + 1);
                throw;
            }
        }

        _completed.AddOrUpdate(id, 1, static (_, count) => count + 1);
    }

    public async Task RunIgnoringAsync(string id, int delayMs)
    {
        _started.AddOrUpdate(id, 1, static (_, count) => count + 1);
        await Task.Delay(delayMs, CancellationToken.None);
        _completed.AddOrUpdate(id, 1, static (_, count) => count + 1);
    }

    Task<bool> ITimerOwners.ExistsAsync(string ownerId, CancellationToken cancellationToken) => Task.FromResult(true);

    Task ITimerHandler.OnDueAsync(TimerDue due, CancellationToken cancellationToken) => RunAsync(due.OwnerId, cancellationToken);
}

public sealed class BlockingProbeHandler(StopProbeControl control) : ICommandHandler<BlockingProbe>
{
    public Task HandleAsync(BlockingProbe command, CancellationToken cancellationToken) => control.RunAsync(command.ProbeId.ToString(), cancellationToken);
}

public sealed class IgnoringProbeHandler(StopProbeControl control) : ICommandHandler<IgnoringProbe>
{
    public Task HandleAsync(IgnoringProbe command, CancellationToken cancellationToken) => control.RunIgnoringAsync(command.ProbeId.ToString(), command.DelayMs);
}

/// <summary>Every log entry the silos of a test write, with its event id and rendered message.</summary>
public sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<(int EventId, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Capture(Entries);

    public void Dispose()
    {
    }

    private sealed class Capture(ConcurrentQueue<(int EventId, string Message)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue((eventId.Id, formatter(state, exception)));
    }
}
