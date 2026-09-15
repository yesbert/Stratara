using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using IRequest = Stratara.Abstractions.Mediator.IRequest;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// Task 4.6: a handler running in one aggregate's turn sends a command for a second aggregate while that
/// aggregate runs another command. The sent command waits for the second aggregate's turn instead of running
/// inside the first one's, so the two commands on the second aggregate never overlap.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class CrossAggregateSendTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const int SlowMs = 1_500;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_command_sent_for_another_aggregate_waits_for_that_aggregates_turn()
    {
        var log = new TurnLog();
        using var app = await StartAsync(log, siloPort: 11200, gatewayPort: 30095);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var slow = DispatchAsync(app.Services, new HoldTurn(second, SlowMs));
        Assert.True(await WaitUntilAsync(() => log.Entries(second).Contains("hold:start")), "the slow command on the second aggregate did not start");

        await DispatchAsync(app.Services, new SendToOther(first, second));
        await slow;

        Assert.Equal(["hold:start", "hold:end", "mark:start", "mark:end"], log.Entries(second));
        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(TurnLog log, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = PocHosting.CreateBuilder();
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.Services
            .AddSessionContext()
            .AddSecurity()
            .AddMediator()
            .AddScoped<ICommandHandler<HoldTurn>, HoldTurnHandler>()
            .AddScoped<ICommandHandler<SendToOther>, SendToOtherHandler>()
            .AddScoped<ICommandHandler<MarkTurn>, MarkTurnHandler>()
            .AddAggregatesFromAssemblyContaining<CrossAggregateSendTests>()
            .AddTrustedType<HoldTurn>()
            .AddTrustedType<SendToOther>()
            .AddTrustedType<MarkTurn>()
            .AddStrataraAggregateGrains()
            .AddSingleton(log);

        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static async Task DispatchAsync<TCommand>(IServiceProvider services, TCommand command) where TCommand : class, IRequest
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.New());
        await scope.ServiceProvider.GetRequiredService<IMediator>().HandleAsync(command);
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

            await Task.Delay(20);
        }

        return false;
    }
}

public sealed record HoldTurn(Guid AggregateId, int DelayMs) : ICommand, IAggregateScopedCommand;

/// <summary>Runs in the first aggregate's turn and sends a command for the second one.</summary>
public sealed record SendToOther(Guid AggregateId, Guid Other) : ICommand, IAggregateScopedCommand;

public sealed record MarkTurn(Guid AggregateId) : ICommand, IAggregateScopedCommand;

public sealed class TurnLog
{
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<string>> _entries = new();

    public void Record(Guid aggregateId, string entry) => _entries.GetOrAdd(aggregateId, _ => new ConcurrentQueue<string>()).Enqueue(entry);

    public IReadOnlyList<string> Entries(Guid aggregateId) =>
        _entries.TryGetValue(aggregateId, out var queue) ? [.. queue] : [];
}

public sealed class HoldTurnHandler(TurnLog log) : ICommandHandler<HoldTurn>
{
    public async Task HandleAsync(HoldTurn command, CancellationToken cancellationToken)
    {
        log.Record(command.AggregateId, "hold:start");
        await Task.Delay(command.DelayMs, cancellationToken);
        log.Record(command.AggregateId, "hold:end");
    }
}

public sealed class SendToOtherHandler(IMediator mediator) : ICommandHandler<SendToOther>
{
    public Task HandleAsync(SendToOther command, CancellationToken cancellationToken) =>
        mediator.HandleAsync(new MarkTurn(command.Other), cancellationToken);
}

public sealed class MarkTurnHandler(TurnLog log) : ICommandHandler<MarkTurn>
{
    public async Task HandleAsync(MarkTurn command, CancellationToken cancellationToken)
    {
        log.Record(command.AggregateId, "mark:start");
        await Task.Delay(50, cancellationToken);
        log.Record(command.AggregateId, "mark:end");
    }
}
