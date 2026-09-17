using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Projections;
using IRequest = Stratara.Abstractions.Mediator.IRequest;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// Scenario <em>A handler sends back to an aggregate in its own chain</em>: A's handler sends to B, B's handler
/// sends to A. A's turn is waiting on B, so a send back to A would wait for a turn that is waiting for it. The send
/// is refused at once with a message naming both aggregates, B's command fails with the refusal, A's command with
/// B's failure — well under the runtime's thirty-second response timeout, which is what the two used to wait for.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class SendCycleTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private static readonly TimeSpan WellUnderTheResponseTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_send_back_into_the_chain_is_refused_at_once_naming_both_aggregates()
    {
        var log = new TurnLog();
        using var app = await StartAsync(log, siloPort: 11312, gatewayPort: 30202);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var started = Stopwatch.GetTimestamp();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => DispatchAsync(app.Services, new SendAround(a, b)));
        var elapsed = Stopwatch.GetElapsedTime(started);

        var refusal = Flatten(failure).FirstOrDefault(ex => ex.Message.Contains("must not form a cycle", StringComparison.Ordinal));
        Assert.True(refusal is not null, $"the failure did not carry the refusal: {failure}");
        Assert.Contains(a.ToString(), refusal.Message, StringComparison.Ordinal);
        Assert.Contains(b.ToString(), refusal.Message, StringComparison.Ordinal);
        Assert.True(elapsed < WellUnderTheResponseTimeout, $"the cycle took {elapsed} to fail; it waited for a timeout");
        Assert.Equal(["around:start"], log.Entries(a));
        Assert.Equal(["back:start"], log.Entries(b));
        TestContext.Current.TestOutputHelper?.WriteLine($"the cycle was refused after {elapsed.TotalMilliseconds:F0} ms");
        await app.StopAsync();
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
                {
                    yield return inner;
                }
            }
        }
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
            .AddScoped<ICommandHandler<SendAround>, SendAroundHandler>()
            .AddScoped<ICommandHandler<SendBack>, SendBackHandler>()
            .AddScoped<ICommandHandler<MarkTurn>, MarkTurnHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<SendAround>()
            .AddTrustedType<SendBack>()
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
}

/// <summary>Runs in A's turn and sends to B.</summary>
public sealed record SendAround(Guid AggregateId, Guid Other) : ICommand, IAggregateScopedCommand;

/// <summary>Runs in B's turn and sends back to A.</summary>
public sealed record SendBack(Guid AggregateId, Guid Other) : ICommand, IAggregateScopedCommand;

public sealed class SendAroundHandler(IMediator mediator, TurnLog log) : ICommandHandler<SendAround>
{
    public Task HandleAsync(SendAround command, CancellationToken cancellationToken)
    {
        log.Record(command.AggregateId, "around:start");
        return mediator.HandleAsync(new SendBack(command.Other, command.AggregateId), cancellationToken);
    }
}

public sealed class SendBackHandler(IMediator mediator, TurnLog log) : ICommandHandler<SendBack>
{
    public Task HandleAsync(SendBack command, CancellationToken cancellationToken)
    {
        log.Record(command.AggregateId, "back:start");
        return mediator.HandleAsync(new MarkTurn(command.Other), cancellationToken);
    }
}
