using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.EventSourcing;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Projections;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.Aggregates;

/// <summary>
/// Scenario <em>The cluster is unstable and activates an aggregate twice</em>, simulated: a directory lapse leaves two
/// activations of one aggregate, which two single-silo clusters with distinct cluster ids on one store reproduce
/// without a harness for split membership. Each activation runs a command for the aggregate that reads its version,
/// waits at a gate and appends; released together, one append is stored and the other is refused with a concurrency
/// conflict. Released one after the other, both are stored — the conflict is the store's version check, not the gate.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class DuplicateActivationTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const string Store = "poc_duplicate_activation_store";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Two_activations_that_append_together_store_one_append_and_refuse_the_other()
    {
        var gate = new AppendGate();
        using var first = await StartAsync(gate, siloPort: 11337, gatewayPort: 30227);
        using var second = await StartAsync(gate, siloPort: 11338, gatewayPort: 30228);
        var (tenantId, aggregateId) = await CreateAsync(first.Services);

        var dispatches = new[] { DispatchAsync(first.Services, tenantId, aggregateId), DispatchAsync(second.Services, tenantId, aggregateId) };
        Assert.True(await gate.WaitForEnteredAsync(2, Timeout), $"both activations did not reach the gate; entered {gate.Entered}");
        gate.Release();
        var outcomes = await Task.WhenAll(dispatches.Select(Outcome));

        Assert.Equal(1, outcomes.Count(outcome => outcome is null));
        var refused = Assert.Single(outcomes, outcome => outcome is not null);
        Assert.True(IsConcurrencyConflict(refused!), $"the second append was refused with something other than a concurrency conflict: {refused}");
        Assert.Equal(2, await VersionAsync(first.Services, tenantId, aggregateId));

        await second.StopAsync();
        await first.StopAsync();
    }

    [Fact]
    public async Task Two_activations_that_append_one_after_the_other_store_both()
    {
        var gate = new AppendGate();
        gate.Release();
        using var first = await StartAsync(gate, siloPort: 11339, gatewayPort: 30229);
        using var second = await StartAsync(gate, siloPort: 11340, gatewayPort: 30230);
        var (tenantId, aggregateId) = await CreateAsync(first.Services);

        Assert.Null(await Outcome(DispatchAsync(first.Services, tenantId, aggregateId)));
        Assert.Null(await Outcome(DispatchAsync(second.Services, tenantId, aggregateId)));
        Assert.Equal(3, await VersionAsync(first.Services, tenantId, aggregateId));

        await second.StopAsync();
        await first.StopAsync();
    }

    private static async Task<Exception?> Outcome(Task dispatch)
    {
        try
        {
            await dispatch;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static bool IsConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ConcurrencyException || current.GetType().Name == nameof(ConcurrencyException))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<(Guid TenantId, Guid AggregateId)> CreateAsync(IServiceProvider services)
    {
        var tenantId = Guid.NewGuid();
        var aggregateId = Guid.NewGuid();
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        var events = scope.ServiceProvider.GetRequiredService<IEventSource>();
        await events.CreateAsync<Counter>(aggregateId, new CounterCreated(aggregateId));
        await events.SaveChangesAsync();
        return (tenantId, aggregateId);
    }

    private static async Task DispatchAsync(IServiceProvider services, Guid tenantId, Guid aggregateId)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        await scope.ServiceProvider.GetRequiredService<IMediator>().HandleAsync(new AppendAtGate(aggregateId));
    }

    private static async Task<long> VersionAsync(IServiceProvider services, Guid tenantId, Guid aggregateId)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(PocSessions.For(tenantId));
        return await scope.ServiceProvider.GetRequiredService<IEventSource>().GetCurrentVersionAsync(aggregateId);
    }

    private async Task<IHost> StartAsync(AppendGate gate, int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor(Store),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        // Each silo port is a cluster of its own: two clusters, one store — two activations of one aggregate.
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddBackendServices();
        builder.Services
            .AddEventSourcing()
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddSingleton(gate)
            .AddScoped<ICommandHandler<AppendAtGate>, AppendAtGateHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<Counter>()
            .AddTrustedType<AppendAtGate>()
            .AddStrataraAggregateGrains();

        var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<PocWriteDbContext>>().CreateDbContextAsync();
            await context.Database.EnsureCreatedAsync();
        }

        using var startTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.StartAsync(startTimeout.Token);
        return app;
    }
}

/// <summary>Appends to its aggregate after the gate opens, so two activations read the same version before either saves.</summary>
public sealed record AppendAtGate(Guid AggregateId) : ICommand, IAggregateScopedCommand;

public sealed class AppendAtGateHandler(IEventSource events, AppendGate gate) : ICommandHandler<AppendAtGate>
{
    public async Task HandleAsync(AppendAtGate command, CancellationToken cancellationToken)
    {
        await events.AppendAsync<Counter>(command.AggregateId, new CounterIncremented(command.AggregateId, 1), cancellationToken);
        await gate.PassAsync(cancellationToken);
        await events.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>A gate the handlers of both hosts wait at, shared because both run in the test's process.</summary>
public sealed class AppendGate
{
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _entered;

    public int Entered => _entered;

    public async Task PassAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _entered);
        await _open.Task.WaitAsync(cancellationToken);
    }

    public void Release() => _open.TrySetResult();

    public async Task<bool> WaitForEnteredAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (_entered < count && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        return _entered >= count;
    }
}
