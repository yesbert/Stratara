using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Stratara.Outbox.RabbitMQ.Outbox;
using Stratara.Outbox.RabbitMQ.Projections;
using Stratara.Abstractions.Outbox;
using Stratara.Abstractions.Projections;

namespace Stratara.Outbox.RabbitMQ.Tests.DependencyInjection;

public class OutboxServiceCollectionExtensionsTests
{
    [Fact]
    public void AddOutboxDispatcher_RegistersScopedDispatchersAndTransitiveProjectionReplayState()
    {
        var services = new ServiceCollection();

        services.AddOutboxDispatcher();

        var commandDescriptor = Assert.Single(services, d => d.ServiceType == typeof(ICommandOutboxDispatcher));
        var bundleDescriptor = Assert.Single(services, d => d.ServiceType == typeof(IEventBundleOutboxDispatcher));
        var replayDescriptor = Assert.Single(services, d => d.ServiceType == typeof(IProjectionReplayState));

        Assert.Equal(ServiceLifetime.Scoped, commandDescriptor.Lifetime);
        Assert.Equal(typeof(CommandOutboxDispatcher), commandDescriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, bundleDescriptor.Lifetime);
        Assert.Equal(typeof(EventBundleOutboxDispatcher), bundleDescriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, replayDescriptor.Lifetime);
        Assert.Equal(typeof(ProjectionReplayState), replayDescriptor.ImplementationType);
    }

    [Fact]
    public void AddProjectionReplayState_IsIdempotent_DueToTryAddSingleton()
    {
        var services = new ServiceCollection();

        services.AddProjectionReplayState();
        services.AddProjectionReplayState();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IProjectionReplayState));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(typeof(ProjectionReplayState), descriptor.ImplementationType);
    }

    [Fact]
    public void AddOutboxWorker_RegistersHostedServiceAndBindsOptions()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:PollingIntervalSeconds"] = "7",
                ["Outbox:BatchSize"] = "500",
                ["Outbox:LockLeaseSeconds"] = "42",
            })
            .Build();

        services.AddOutboxWorker(configuration);

        var hostedDescriptor = Assert.Single(services, d => d.ImplementationType == typeof(OutboxWorker));
        Assert.Equal(ServiceLifetime.Singleton, hostedDescriptor.Lifetime);
        Assert.Equal(typeof(IHostedService), hostedDescriptor.ServiceType);

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<OutboxOptions>>().Value;
        Assert.Equal(7, options.PollingIntervalSeconds);
        Assert.Equal(500, options.BatchSize);
        Assert.Equal(42, options.LockLeaseSeconds);
    }

    [Fact]
    public void AddOutboxWorker_DefaultsToNullOutboxLock()
    {
        var services = new ServiceCollection();

        services.AddOutboxWorker(new ConfigurationBuilder().Build());

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IOutboxLock));
        Assert.Equal(typeof(NullOutboxLock), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddRedisOutboxLock_ReplacesNullOutboxLockWithRedisOutboxLock()
    {
        var services = new ServiceCollection();
        services.AddOutboxWorker(new ConfigurationBuilder().Build());

        services.AddRedisOutboxLock();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IOutboxLock));
        Assert.Equal(typeof(RedisOutboxLock), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddRedisOutboxLock_RemovesAllPriorRegistrationsBeforeAdding()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOutboxLock, NullOutboxLock>();
        services.AddSingleton<IOutboxLock, NullOutboxLock>();

        services.AddRedisOutboxLock();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IOutboxLock));
        Assert.Equal(typeof(RedisOutboxLock), descriptor.ImplementationType);
    }

    [Fact]
    public void AddOutboxDispatcher_PlusAddOutboxWorker_DoesNotDoubleRegisterReplayState()
    {
        var services = new ServiceCollection();

        services.AddOutboxDispatcher();
        services.AddProjectionReplayState();

        Assert.Single(services, d => d.ServiceType == typeof(IProjectionReplayState));
    }
}
