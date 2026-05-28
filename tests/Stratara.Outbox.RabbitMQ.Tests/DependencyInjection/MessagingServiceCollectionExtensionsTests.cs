using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Outbox.RabbitMQ.Messaging;
using Stratara.Abstractions.Messaging;
using Stratara.Shared.Messaging;

namespace Stratara.Outbox.RabbitMQ.Tests.DependencyInjection;

public class MessagingServiceCollectionExtensionsTests
{
    [Fact]
    public void AddMessaging_RegistersRabbitMqBusAsSingletonMessageBus()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.AddMessaging();

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IMessageBus));
        Assert.Equal(typeof(RabbitMqBus), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddMessaging_RegistersMessagingIdentifierAsSingleton()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        builder.AddMessaging();

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IMessagingIdentifier));
        Assert.Equal(typeof(MessagingIdentifier), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddMessaging_BindsMessagingOptionsFromMessagingSection()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Messaging:Topics:0:Name"] = "Command",
            ["Messaging:Topics:0:Value"] = "stratara.command",
            ["Messaging:Topics:0:Subscriptions:0:Name"] = "Worker",
            ["Messaging:Topics:0:Subscriptions:0:Value"] = "stratara.command.worker",
        });

        builder.AddMessaging();

        var sp = builder.Services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>().Value;
        Assert.Equal("stratara.command", options.GetTopicValue("Command"));
        Assert.Equal("stratara.command.worker", options.GetSubscriptionValue("Command", "Worker"));
    }

    [Fact]
    public void AddMessaging_ReturnsSameBuilderForChaining()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var returned = builder.AddMessaging();

        Assert.Same(builder, returned);
    }
}
