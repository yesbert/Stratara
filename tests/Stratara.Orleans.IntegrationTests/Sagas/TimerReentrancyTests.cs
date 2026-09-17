using System.Collections.Concurrent;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.CommitOrder;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Session;
using Stratara.Abstractions.Timers;
using Stratara.Orleans.CommitOrder;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Hosting.Scenarios;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.Sagas;
using Stratara.Orleans.Timers;
using Stratara.Sagas.Abstractions;

namespace Stratara.Orleans.IntegrationTests.Sagas;

/// <summary>
/// Task 5.1: a fact and a timeout for one process collide, and the timeout reschedules itself. The fact's step
/// holds the process past the tick's due time and then touches the process's timers, while the tick waits for
/// the process; the first tick reschedules from inside its own handling, and the second completes the process,
/// which cancels its timers. Without a reentrant timer owner each of these calls waits on the tick's own turn
/// until the call times out. Both steps complete, each tick is handled once, and no timer is left behind.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class TimerReentrancyTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task A_colliding_fact_and_a_rescheduling_timeout_both_complete_and_each_tick_runs_once()
    {
        var log = new TickLog();
        using var app = await StartAsync(log, siloPort: 11271, gatewayPort: 30161);
        var tenantId = Guid.NewGuid();
        var counterId = Guid.NewGuid();

        await AppendAsync(app.Services, tenantId, counterId, new CounterCreated(counterId), create: true);
        Assert.True(await WaitUntilAsync(() => log.Steps(counterId).Contains("started")), "the process did not start");

        await AppendAsync(app.Services, tenantId, counterId, new CounterIncremented(counterId, 1), create: false);
        Assert.True(
            await WaitUntilAsync(() => log.Steps(counterId).Contains("tick:2")),
            $"the process did not reach its second tick; steps: {string.Join(", ", log.Steps(counterId))}");

        var timers = app.Services.GetRequiredService<IDurableTimers>();
        var owner = SagaProcessTimerHost.OwnerOf(SagaProcessKey.Of(nameof(TickingSaga), counterId));
        Assert.True(await WaitUntilAsync(() => timers.ListAsync(owner).Result.Count == 0), "the completed process left a timer behind");
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.Equal(["started", "fact:start", "fact:end", "tick:1", "tick:2"], log.Steps(counterId));
        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(TickLog log, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_ticking_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddSagaServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocCommitOrderWriteDbContext>()
            .AddNpgsqlReadDbContextFactoryOn<PocReadDbContext>(postgres.ConnectionStringFor("poc_ticking_read"))
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<CounterCreated>()
            .AddTrustedType<CounterIncremented>()
            .AddTrustedType<TickingState>()
            .AddTrustedType<TickingStarted>()
            .AddTrustedType<TickingFactSeen>()
            .AddTrustedType<Ticked>()
            .AddTrustedType<TickingFinished>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(log)
            .AddScoped<ISaga, TickingSaga>()
            .Configure<PocCounterOptions>(options => options.MaintainPartitionCounter = false)
            .AddScoped<ICommittedPositionReader, PostgresTransactionIdReader<PocCommitOrderWriteDbContext>>()
            .AddStrataraProjectionCheckpoints<PocReadDbContext>()
            .AddStrataraSagaGrains(options =>
            {
                options.PollInterval = TimeSpan.FromSeconds(1);
                options.KeepAlivePeriod = TimeSpan.FromSeconds(5);
            })
            .Configure<DurableTimerOptions>(options => options.RetryPeriod = TimeSpan.FromSeconds(1));
        builder.Services.Configure<DurableTimerOptions>(options => options.DueTolerance = TimeSpan.Zero);

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using (var write = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocCommitOrderWriteDbContext>>().CreateDbContextAsync())
            {
                await write.Database.EnsureCreatedAsync();
            }

            await using var read = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocReadDbContext>>().CreateDbContextAsync();
            await read.Database.EnsureCreatedAsync();
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task AppendAsync(IServiceProvider services, Guid tenantId, Guid streamId, object @event, bool create)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        if (create)
        {
            await events.CreateAsync<Counter>(streamId, @event);
        }
        else
        {
            await events.AppendAsync<Counter>(streamId, @event);
        }

        await events.SaveChangesAsync();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
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
}

public sealed record TickingStarted(Guid CounterId, DateTimeOffset FirstTickAt);

public sealed record TickingFactSeen(Guid CounterId);

public sealed record Ticked(Guid CounterId);

public sealed record TickingFinished(Guid CounterId);

public sealed class TickingState : ISagaProcessState
{
    public Guid Id { get; set; }

    public DateTimeOffset FirstTickAt { get; set; }

    public bool FactSeen { get; set; }

    public int Ticks { get; set; }

    public bool Finished { get; set; }

    public bool Completed => Finished;

    [UsedImplicitly]
    public void Apply(TickingStarted @event)
    {
        Id = @event.CounterId;
        FirstTickAt = @event.FirstTickAt;
    }

    [UsedImplicitly]
    public void Apply(TickingFactSeen @event) => FactSeen = true;

    [UsedImplicitly]
    public void Apply(Ticked @event) => Ticks++;

    [UsedImplicitly]
    public void Apply(TickingFinished @event) => Finished = true;
}

/// <summary>Every step a process handled, in the order it handled them.</summary>
public sealed class TickLog
{
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<string>> _steps = new();

    public void Record(Guid counterId, string step) => _steps.GetOrAdd(counterId, _ => new ConcurrentQueue<string>()).Enqueue(step);

    public IReadOnlyList<string> Steps(Guid counterId) => _steps.TryGetValue(counterId, out var steps) ? [.. steps] : [];
}

/// <summary>
/// Starts on a counter's creation with a tick three seconds later. A fact on the counter holds the process until
/// the tick is past due and then cancels a timer. The first tick reschedules itself a second later; the second
/// finishes the process.
/// </summary>
public sealed class TickingSaga(TimeProvider timeProvider, TickLog log) : SagaProcess<TickingState>
{
    private const string TickPurpose = "tick";
    private const string NotePurpose = "note";
    private static readonly TimeSpan FirstTickAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan HoldPastTheTick = TimeSpan.FromSeconds(2);

    public override bool Handles(IEvent @event) => @event.Data is CounterCreated or CounterIncremented;

    public override Guid CorrelationOf(IEvent @event) => @event.Data switch
    {
        CounterCreated created => created.CounterId,
        CounterIncremented incremented => incremented.CounterId,
        _ => throw new ArgumentOutOfRangeException(nameof(@event)),
    };

    public override async Task HandleAsync(TickingState state, IEvent @event, ISagaProcessContext context, CancellationToken cancellationToken)
    {
        switch (@event.Data)
        {
            case CounterCreated created when state.Id == Guid.Empty:
            {
                var firstTickAt = timeProvider.GetUtcNow() + FirstTickAfter;
                log.Record(created.CounterId, "started");
                context.Emit(new TickingStarted(created.CounterId, firstTickAt));
                context.Schedule(TickPurpose, firstTickAt);
                break;
            }

            case CounterIncremented incremented when !state.FactSeen:
            {
                log.Record(incremented.CounterId, "fact:start");
                var hold = state.FirstTickAt + HoldPastTheTick - timeProvider.GetUtcNow();
                if (hold > TimeSpan.Zero)
                {
                    await Task.Delay(hold, cancellationToken);
                }

                context.Cancel(NotePurpose);
                log.Record(incremented.CounterId, "fact:end");
                context.Emit(new TickingFactSeen(incremented.CounterId));
                break;
            }
        }
    }

    public override Task OnTimeoutAsync(TickingState state, string purpose, ISagaProcessContext context, CancellationToken cancellationToken)
    {
        if (purpose != TickPurpose || state.Finished)
        {
            return Task.CompletedTask;
        }

        log.Record(state.Id, $"tick:{state.Ticks + 1}");
        context.Emit(new Ticked(state.Id));
        if (state.Ticks == 0)
        {
            context.Schedule(TickPurpose, timeProvider.GetUtcNow() + TimeSpan.FromSeconds(1));
        }
        else
        {
            context.Emit(new TickingFinished(state.Id));
        }

        return Task.CompletedTask;
    }
}
