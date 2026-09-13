using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// T3 of the expectations: approve, then cancel, dispatched back to back without waiting for the
/// first, arrive at the aggregate in that order every time — on 500 aggregates with one pair each,
/// and on one aggregate with 500 pairs. A competing-consumer queue makes no such promise; the grain's
/// turn does.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class ArrivalOrderTests(PostgreSqlFixture postgres, RedisFixture redis)
{
    private const string OrleansDatabase = "poc_orleans";
    private const int Pairs = 500;

    [Fact]
    public async Task Pairs_on_distinct_aggregates_arrive_in_dispatch_order()
    {
        var log = new ArrivalLog();
        using var app = await StartAsync(log, siloPort: 11161, gatewayPort: 30050);
        var aggregates = Enumerable.Range(0, Pairs).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(aggregates.Select(aggregateId => DispatchPairAsync(app.Services, aggregateId, pair: 0)));

        var reordered = aggregates.Where(id => !log.Sequence(id).SequenceEqual(["approve:0", "cancel:0"])).ToList();
        Assert.True(reordered.Count == 0, $"{reordered.Count} of {Pairs} pairs arrived out of order.");
        await app.StopAsync();
    }

    [Fact]
    public async Task Pairs_on_one_aggregate_arrive_in_dispatch_order()
    {
        var log = new ArrivalLog();
        using var app = await StartAsync(log, siloPort: 11162, gatewayPort: 30051);
        var aggregateId = Guid.NewGuid();

        await using var scope = app.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(NewSession());
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var inFlight = new List<Task>(Pairs * 2);
        for (var pair = 0; pair < Pairs; pair++)
        {
            inFlight.Add(mediator.HandleAsync(new Approve(aggregateId, pair)));
            inFlight.Add(mediator.HandleAsync(new Cancel(aggregateId, pair)));
        }

        await Task.WhenAll(inFlight);

        var expected = Enumerable.Range(0, Pairs).SelectMany(pair => new[] { $"approve:{pair}", $"cancel:{pair}" });
        Assert.Equal(expected, log.Sequence(aggregateId));
        await app.StopAsync();
    }

    private static async Task DispatchPairAsync(IServiceProvider services, Guid aggregateId, int pair)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(NewSession());
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var approve = mediator.HandleAsync(new Approve(aggregateId, pair));
        var cancel = mediator.HandleAsync(new Cancel(aggregateId, pair));
        await Task.WhenAll(approve, cancel);
    }

    private async Task<IHost> StartAsync(ArrivalLog log, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor(OrleansDatabase);
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.Services
            .AddSessionContext()
            .AddSecurity()
            .AddMediator()
            .AddScoped<ICommandHandler<Approve>, ApproveHandler>()
            .AddScoped<ICommandHandler<Cancel>, CancelHandler>()
            .AddAggregatesFromAssemblyContaining<ArrivalOrderTests>()
            .AddTrustedType<Approve>()
            .AddTrustedType<Cancel>()
            .AddStrataraAggregateGrains()
            .AddSingleton(log);

        var app = builder.Build();
        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }

    private static SessionContext NewSession()
    {
        var tenantId = Guid.NewGuid();
        return new SessionContext(Guid.CreateVersion7().ToString("N"), null, null, tenantId, tenantId, tenantId, null);
    }
}

public sealed record Approve(Guid AggregateId, int Pair) : ICommand, IAggregateScopedCommand;

public sealed record Cancel(Guid AggregateId, int Pair) : ICommand, IAggregateScopedCommand;

public sealed class ArrivalLog
{
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<string>> _arrivals = new();

    public void Record(Guid aggregateId, string step) => _arrivals.GetOrAdd(aggregateId, _ => new ConcurrentQueue<string>()).Enqueue(step);

    public IReadOnlyList<string> Sequence(Guid aggregateId) =>
        _arrivals.TryGetValue(aggregateId, out var queue) ? [.. queue] : [];
}

public sealed class ApproveHandler(ArrivalLog log) : ICommandHandler<Approve>
{
    public Task HandleAsync(Approve command, CancellationToken cancellationToken)
    {
        log.Record(command.AggregateId, $"approve:{command.Pair}");
        return Task.CompletedTask;
    }
}

public sealed class CancelHandler(ArrivalLog log) : ICommandHandler<Cancel>
{
    public Task HandleAsync(Cancel command, CancellationToken cancellationToken)
    {
        log.Record(command.AggregateId, $"cancel:{command.Pair}");
        return Task.CompletedTask;
    }
}
