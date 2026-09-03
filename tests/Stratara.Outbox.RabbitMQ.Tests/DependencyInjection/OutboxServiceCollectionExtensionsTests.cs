using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Diagnostics;
using Moq;
using Microsoft.Extensions.Logging;
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
        Assert.NotNull(replayDescriptor.ImplementationFactory);
    }

    [Fact]
    public void AddProjectionReplayState_IsIdempotent_DueToTryAddSingleton()
    {
        var services = new ServiceCollection();

        services.AddProjectionReplayState();
        services.AddProjectionReplayState();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IProjectionReplayState));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.NotNull(descriptor.ImplementationFactory);
    }

    [Fact]
    public void AddProjectionReplayState_WithoutRedis_ResolvesInProcessStateAndWarnsOnce()
    {
        var services = new ServiceCollection();
        var logs = new List<(LogLevel Level, EventId Id)>();
        services.AddLogging(b => b.AddProvider(new CapturingLoggerProvider(logs)));
        services.AddProjectionReplayState();
        var sp = services.BuildServiceProvider();

        var first = sp.GetRequiredService<IProjectionReplayState>();
        var second = sp.GetRequiredService<IProjectionReplayState>();

        Assert.IsType<InProcessProjectionReplayState>(first);
        Assert.Same(first, second);
        Assert.Single(logs, l => l.Level == LogLevel.Warning && l.Id.Id == LogEvents.Projection.ProjectionReplayCoordinationInProcess);
    }

    [Fact]
    public void AddProjectionReplayState_WithoutLogging_StillResolvesInProcessState()
    {
        var services = new ServiceCollection();
        services.AddProjectionReplayState();
        var sp = services.BuildServiceProvider();

        Assert.IsType<InProcessProjectionReplayState>(sp.GetRequiredService<IProjectionReplayState>());
    }

    [Fact]
    public void AddProjectionReplayState_RedisRegisteredBefore_ResolvesRedisStateWithoutWarning()
    {
        var services = new ServiceCollection();
        var logs = new List<(LogLevel Level, EventId Id)>();
        services.AddLogging(b => b.AddProvider(new CapturingLoggerProvider(logs)));
        services.AddSingleton(Mock.Of<IConnectionMultiplexer>());
        services.AddProjectionReplayState();
        var sp = services.BuildServiceProvider();

        Assert.IsType<ProjectionReplayState>(sp.GetRequiredService<IProjectionReplayState>());
        Assert.DoesNotContain(logs, l => l.Id.Id == LogEvents.Projection.ProjectionReplayCoordinationInProcess);
    }

    [Fact]
    public void AddProjectionReplayState_RedisRegisteredAfter_ResolvesRedisState()
    {
        var services = new ServiceCollection();
        services.AddProjectionReplayState();
        services.AddSingleton(Mock.Of<IConnectionMultiplexer>());
        var sp = services.BuildServiceProvider();

        Assert.IsType<ProjectionReplayState>(sp.GetRequiredService<IProjectionReplayState>());
    }

    [Fact]
    public void AddProjectionReplayState_ConsumerStateRegisteredBefore_Wins()
    {
        var services = new ServiceCollection();
        var own = Mock.Of<IProjectionReplayState>();
        services.AddSingleton(own);
        services.AddProjectionReplayState();
        var sp = services.BuildServiceProvider();

        Assert.Same(own, sp.GetRequiredService<IProjectionReplayState>());
    }

    [Fact]
    public void AddProjectionReplayState_ConsumerStateRegisteredAfter_Wins()
    {
        var services = new ServiceCollection();
        var own = Mock.Of<IProjectionReplayState>();
        services.AddProjectionReplayState();
        services.AddSingleton(own);
        var sp = services.BuildServiceProvider();

        Assert.Same(own, sp.GetRequiredService<IProjectionReplayState>());
    }

    private sealed class CapturingLoggerProvider(List<(LogLevel Level, EventId Id)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);
        public void Dispose() { }

        private sealed class CapturingLogger(List<(LogLevel Level, EventId Id)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (sink)
                {
                    sink.Add((logLevel, eventId));
                }
            }
        }
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
