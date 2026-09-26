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
    public void AddAzureServiceBus_RegistersTheDrainOfStoppingSubscriptionsOnce()
    {
        var services = new ServiceCollection();

        services.AddAzureServiceBus(SampleConnectionString);
        services.AddAzureServiceBusWithManagedIdentity("example.servicebus.windows.net");

        Assert.Single(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AzureServiceBusSubscriptionsDrain));
    }

    [Fact]
    public async Task TheDrainOfATransportThatDoesNotHoldTheBus_HasNothingToWaitFor()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddMessaging();
        builder.Services.AddAzureServiceBus(SampleConnectionString);
        await using var provider = builder.Services.BuildServiceProvider();

        var drains = provider.GetServices<IHostedService>().OfType<IHostedLifecycleService>().ToList();

        Assert.Equal(2, drains.Count);
        foreach (var drain in drains)
        {
            await drain.StoppedAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }
}
