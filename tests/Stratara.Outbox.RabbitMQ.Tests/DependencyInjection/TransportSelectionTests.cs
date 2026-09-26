using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Messaging;
using Stratara.Outbox.AzureServiceBus.Messaging;
using Stratara.Outbox.RabbitMQ.Messaging;

namespace Stratara.Outbox.RabbitMQ.Tests.DependencyInjection;

public class TransportSelectionTests
{
    private const string AzureServiceBusBusTypeName = "Stratara.Outbox.AzureServiceBus.Messaging.AzureServiceBusBus";
    private const string SampleConnectionString =
        "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v";

    [Fact]
    public async Task AddAzureServiceBus_AfterAddMessaging_OverridesRabbitMqAsTheMessageBus()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddMessaging();   // RabbitMQ umbrella claims IMessageBus first

        builder.Services.AddAzureServiceBus(SampleConnectionString);

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IMessageBus));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.Equal(AzureServiceBusBusTypeName, await ResolvedBusTypeNameAsync(builder.Services));
    }

    private static async Task<string?> ResolvedBusTypeNameAsync(IServiceCollection services)
    {
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IMessageBus>().GetType().FullName;
    }

    [Fact]
    public async Task AddAzureServiceBus_OnAnEmptyCollection_RegistersItselfAsTheMessageBus()
    {
        var services = new ServiceCollection();

        services.AddAzureServiceBus(SampleConnectionString);

        Assert.Single(services, d => d.ServiceType == typeof(IMessageBus));
        Assert.Equal(AzureServiceBusBusTypeName, await ResolvedBusTypeNameAsync(services));
    }

    [Fact]
    public async Task AddAzureServiceBusWithManagedIdentity_AfterAddMessaging_OverridesRabbitMq()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddMessaging();

        builder.Services.AddAzureServiceBusWithManagedIdentity("example.servicebus.windows.net");

        Assert.Single(builder.Services, d => d.ServiceType == typeof(IMessageBus));
        Assert.Equal(AzureServiceBusBusTypeName, await ResolvedBusTypeNameAsync(builder.Services));
    }

    [Fact]
    public void AddAzureServiceBus_RegistersTheDrainOfStoppingSubscriptionsOnce()
    {
        var services = new ServiceCollection();

        services.AddAzureServiceBus(SampleConnectionString);
        services.AddAzureServiceBusWithManagedIdentity("example.servicebus.windows.net");

        Assert.Single(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AzureServiceBusSubscriptionsDrain));
    }

    [Fact]
    public async Task TheDrains_WithoutAStoppedSubscription_HaveNothingToWaitFor()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddMessaging();
        builder.Services.AddAzureServiceBus(SampleConnectionString);
        await using var provider = builder.Services.BuildServiceProvider();

        var drains = provider.GetServices<IHostedService>().OfType<IHostedLifecycleService>().ToList();

        Assert.Equal(2, drains.Count);
        foreach (var drain in drains)
        {
            await drain.StoppingAsync(TestContext.Current.CancellationToken);
            await drain.StoppedAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_decorated_message_bus_still_reaches_the_bus_the_drain_waits_for()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddMessaging();
        var inner = builder.Services.Last(d => d.ServiceType == typeof(IMessageBus));
        builder.Services.Remove(inner);
        builder.Services.AddSingleton<IMessageBus>(provider => new DecoratedBus((IMessageBus)inner.ImplementationFactory!(provider)));
        await using var provider = builder.Services.BuildServiceProvider();

        var decorated = Assert.IsType<DecoratedBus>(provider.GetRequiredService<IMessageBus>());

        Assert.Same(provider.GetRequiredService<RabbitMqBus>(), decorated.Inner);
    }

    private sealed class DecoratedBus(IMessageBus inner) : IMessageBus
    {
        public IMessageBus Inner => inner;

        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default) =>
            inner.PublishAsync(topic, message, cancellationToken);

        public Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default) =>
            inner.SubscribeAsync(topic, subscription, handler, cancellationToken);

        public Task EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken = default) =>
            inner.EnsureSubscriptionAsync(topic, subscription, cancellationToken);
    }
}
