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
    public void AddAzureServiceBus_AfterAddMessaging_OverridesRabbitMqAsTheMessageBus()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddMessaging();   // RabbitMQ umbrella claims IMessageBus first

        builder.Services.AddAzureServiceBus(SampleConnectionString);

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IMessageBus));
        Assert.Equal(AzureServiceBusBusTypeName, descriptor.ImplementationType?.FullName);
        Assert.NotEqual(typeof(RabbitMqBus), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddAzureServiceBus_OnAnEmptyCollection_RegistersItselfAsTheMessageBus()
    {
        var services = new ServiceCollection();

        services.AddAzureServiceBus(SampleConnectionString);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IMessageBus));
        Assert.Equal(AzureServiceBusBusTypeName, descriptor.ImplementationType?.FullName);
    }

    [Fact]
    public void AddAzureServiceBusWithManagedIdentity_AfterAddMessaging_OverridesRabbitMq()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddMessaging();

        builder.Services.AddAzureServiceBusWithManagedIdentity("example.servicebus.windows.net");

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IMessageBus));
        Assert.Equal(AzureServiceBusBusTypeName, descriptor.ImplementationType?.FullName);
    }

    [Fact]
    public async Task AddAzureServiceBus_RegistersTheStoppingSubscriptionsOnceAsAHostedService()
    {
        var services = new ServiceCollection().AddLogging();

        services.AddAzureServiceBus(SampleConnectionString);
        services.AddAzureServiceBusWithManagedIdentity("example.servicebus.windows.net");

        await using var provider = services.BuildServiceProvider();
        var stops = Assert.Single(provider.GetServices<IHostedService>().OfType<AzureServiceBusSubscriptionStops>());
        Assert.Same(provider.GetRequiredService<AzureServiceBusSubscriptionStops>(), stops);
    }

    [Fact]
    public async Task TheStoppingSubscriptions_WithoutAStoppedSubscription_HaveNothingToWaitFor()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddMessaging();
        builder.Services.AddAzureServiceBus(SampleConnectionString);
        await using var provider = builder.Services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().OfType<IHostedLifecycleService>().ToList();

        Assert.Equal(2, hosted.Count);
        foreach (var service in hosted)
        {
            await service.StartedAsync(TestContext.Current.CancellationToken);
            await service.StoppingAsync(TestContext.Current.CancellationToken);
            await service.StoppedAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// The hosted service that waits for stopping subscriptions needs nothing the bus needs, so a host that replaced
    /// the message bus — a test host with a fake and a placeholder connection string — starts as before.
    /// </summary>
    [Fact]
    public async Task A_host_that_replaced_the_message_bus_starts_without_building_the_transport()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddAzureServiceBus("not a connection string");
        builder.Services.AddSingleton<IMessageBus, FakeBus>();
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.IsType<FakeBus>(host.Services.GetRequiredService<IMessageBus>());
    }

    private sealed class FakeBus : IMessageBus
    {
        public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SubscribeAsync<T>(string topic, string subscription, Func<T, Task> handler, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureSubscriptionAsync(string topic, string subscription, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
