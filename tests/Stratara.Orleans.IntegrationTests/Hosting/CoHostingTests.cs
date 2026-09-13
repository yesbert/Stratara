using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using StackExchange.Redis;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Persistence;
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;
using Stratara.Orleans.IntegrationTests.Fixtures;
using Stratara.Orleans.IntegrationTests.Store;

namespace Stratara.Orleans.IntegrationTests.Hosting;

/// <summary>
/// Task 3.1, the first stop criterion: an existing Stratara worker composite and an Orleans silo with
/// a Redis grain directory register in one host, in either order, with no change to any existing
/// composite. The host is the real thing — the command worker subscribes to RabbitMQ, the write
/// store is PostgreSQL, the silo's directory is Redis — and the test dispatches through the mediator
/// and calls a grain once it is up.
/// </summary>
[Collection(InfrastructureCollection.Name)]
public sealed class CoHostingTests(PostgreSqlFixture postgres, RedisFixture redis, RabbitMqFixture rabbit)
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);

    [Fact]
    public Task StrataraFirst_ThenOrleans() => RunAsync(strataraFirst: true, siloPort: 11111, gatewayPort: 30000);

    [Fact]
    public Task OrleansFirst_ThenStratara() => RunAsync(strataraFirst: false, siloPort: 11121, gatewayPort: 30010);

    private async Task RunAsync(bool strataraFirst, int siloPort, int gatewayPort)
    {
        await redis.FlushAsync();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:defaultdb"] = postgres.ConnectionString,
            ["ConnectionStrings:rabbitmq"] = rabbit.ConnectionString,
        });

        if (strataraFirst)
        {
            AddStratara(builder);
            AddOrleans(builder, siloPort, gatewayPort);
        }
        else
        {
            AddOrleans(builder, siloPort, gatewayPort);
            AddStratara(builder);
        }

        using var host = builder.Build();
        await EnsureSchemaAsync(host.Services);

        using var startTimeout = new CancellationTokenSource(StartTimeout);
        await host.StartAsync(startTimeout.Token);
        try
        {
            await DispatchThroughMediatorAsync(host.Services);
            await CallGrainAsync(host.Services);
            await TouchStoreAsync(host.Services);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static void AddStratara(HostApplicationBuilder builder)
    {
        builder.AddCommandWorkerServices();
        builder.Services
            .AddNpgsqlWriteDbContextFactory<PocWriteDbContext>()
            .AddCommandHandlersFromAssemblyContaining<CoHostingTests>()
            .AddAggregatesFromAssemblyContaining<CoHostingTests>()
            .AddSingleton<CommandLog>();
    }

    private void AddOrleans(HostApplicationBuilder builder, int siloPort, int gatewayPort)
    {
        var redisOptions = ConfigurationOptions.Parse(redis.ConnectionString);
        builder.UseOrleans(silo =>
        {
            silo.UseLocalhostClustering(siloPort, gatewayPort);
            silo.UseRedisGrainDirectoryAsDefault(options => options.ConfigurationOptions = redisOptions);
        });
    }

    private static async Task EnsureSchemaAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await using var context = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<PocWriteDbContext>>()
            .CreateDbContextAsync();
        await context.Database.EnsureCreatedAsync();
    }

    private static async Task DispatchThroughMediatorAsync(IServiceProvider services)
    {
        var aggregateId = Guid.NewGuid();

        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(NewSession());
        await scope.ServiceProvider.GetRequiredService<IMediator>().HandleAsync(new PingCommand(aggregateId));

        Assert.Contains(aggregateId, services.GetRequiredService<CommandLog>().Handled);
    }

    private async Task CallGrainAsync(IServiceProvider services)
    {
        var reply = await services.GetRequiredService<IGrainFactory>().GetGrain<IPingGrain>("co-hosting").PingAsync();

        Assert.Equal("pong from co-hosting", reply);
        Assert.True(redis.CountKeys() > 0, "The silo answered but left nothing in Redis, so the grain directory in use is not the Redis one.");
    }

    private static async Task TouchStoreAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISessionContextProvider>().Set(NewSession());
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWriteUnitOfWork>();
        await using var transaction = await unitOfWork.StartAsync();

        var exists = await unitOfWork.CreateEventStreamRepository(transaction).StreamExistsAsync(Guid.NewGuid());

        Assert.False(exists);
    }

    private static SessionContext NewSession()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        return new SessionContext(
            Guid.CreateVersion7().ToString("N"),
            null,
            null,
            tenantId,
            userId,
            tenantId,
            userId);
    }
}

public sealed record PingCommand(Guid AggregateId) : ICommand, IAggregateScopedCommand;

public sealed class CommandLog
{
    public ConcurrentBag<Guid> Handled { get; } = [];
}

public sealed class PingCommandHandler(CommandLog log) : ICommandHandler<PingCommand>
{
    public Task HandleAsync(PingCommand command, CancellationToken cancellationToken)
    {
        log.Handled.Add(command.AggregateId);
        return Task.CompletedTask;
    }
}
