using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Hosting;
using Stratara.Orleans.IntegrationTests.Store;
using Stratara.Orleans.IntegrationTests.Projections;

namespace Stratara.Orleans.IntegrationTests.HeavyWork;

/// <summary>
/// A heavy command runs outside its aggregate's turn and order: a command one scope dispatches for the same
/// aggregate after it is handed over and runs while the heavy command still runs.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class HeavyOrderTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private const int HeavyDelayMs = 6_000;
    private static readonly TimeSpan WellBeforeTheHeavyUnitEnds = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task A_command_after_a_heavy_command_on_its_aggregate_does_not_wait_for_it()
    {
        using var app = await StartAsync(siloPort: 11302, gatewayPort: 30192);
        var aggregateId = Guid.NewGuid();
        var handled = new InteractiveSignal();

        var started = Stopwatch.GetTimestamp();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(NewSession());
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICommandOutboxDispatcher>();
            await dispatcher.EnqueueCommandAsync(new HeavyProbe(aggregateId, HeavyDelayMs));
            await dispatcher.EnqueueCommandAsync(new SignalledProbe(aggregateId, handled.Id));
        }

        var ran = await Task.WhenAny(InteractiveSignal.WaitAsync(handled.Id), Task.Delay(TimeSpan.FromSeconds(HeavyDelayMs / 1000 * 2))) == InteractiveSignal.WaitAsync(handled.Id);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(ran, "the command after the heavy command never ran");
        Assert.True(elapsed < WellBeforeTheHeavyUnitEnds, $"the command after the heavy command ran {elapsed.TotalMilliseconds:F0} ms after the heavy dispatch; it waited for the heavy unit of {HeavyDelayMs} ms");
        await app.StopAsync();
    }

    private async Task<IHost> StartAsync(int siloPort, int gatewayPort)
    {
        var orleansConnectionString = postgres.ConnectionStringFor("poc_orleans");
        await PocSilo.EnsureSchemaAsync(orleansConnectionString);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionStringFor("poc_heavy_store"),
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });
        builder.UseOrleans(silo => PocSilo.Configure(silo, orleansConnectionString, redis.ConnectionString, siloPort, gatewayPort));
        builder.AddBackendServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddScoped<ICommandHandler<SignalledProbe>, SignalledProbeHandler>()
            .AddScoped<ICommandHandler<HeavyProbe>, HeavyProbeHandler>()
            .AddAggregatesFromAssemblyContaining<Counter>()
            .AddTrustedType<SignalledProbe>()
            .AddTrustedType<HeavyProbe>()
            .AddStrataraAggregateGrains()
            .AddStrataraOrleansCommandDispatcher()
            .AddStrataraIntentStore<PocWriteDbContext>();

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

    private static SessionContext NewSession()
    {
        var tenantId = Guid.NewGuid();
        return new SessionContext(Guid.CreateVersion7().ToString("N"), null, null, tenantId, tenantId, tenantId, null);
    }
}

/// <summary>A command for an aggregate whose handler signals that it ran.</summary>
public sealed record SignalledProbe(Guid AggregateId, Guid SignalId) : ICommand, IAggregateScopedCommand;

public sealed class SignalledProbeHandler : ICommandHandler<SignalledProbe>
{
    public Task HandleAsync(SignalledProbe command, CancellationToken cancellationToken)
    {
        InteractiveSignal.Complete(command.SignalId);
        return Task.CompletedTask;
    }
}

/// <summary>The signals of <see cref="SignalledProbe"/>, shared between the test and the silo it hosts in-process.</summary>
public sealed class InteractiveSignal
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, TaskCompletionSource> Signals = new();

    public Guid Id { get; } = Guid.NewGuid();

    public static Task WaitAsync(Guid id) => Signals.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public static void Complete(Guid id) => Signals.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}
