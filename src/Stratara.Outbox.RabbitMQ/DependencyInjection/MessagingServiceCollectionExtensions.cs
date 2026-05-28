using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Outbox.RabbitMQ.Messaging;
using Stratara.Abstractions.Messaging;
using Stratara.Shared.Messaging;
using Microsoft.Extensions.Configuration;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Service-collection extensions for the Stratara messaging stack.</summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the messaging infrastructure: <see cref="IMessageBus"/> backed by
    /// <see cref="RabbitMqBus"/>, the <c>IMessagingIdentifier</c> service, and binds
    /// <see cref="MessagingOptions"/> from the <c>Messaging</c> configuration section.
    /// </summary>
    /// <param name="builder">The host-application builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <example>
    /// Bind the RabbitMQ bus from configuration. The composite worker DI extensions
    /// (<c>AddBackendServices</c>, <c>AddCommandWorkerServices</c>, …) already call
    /// this internally; only call it directly when composing a custom worker shape.
    /// <code>
    /// // appsettings.json:
    /// // "Messaging": { "Host": "rabbitmq", "VirtualHost": "/", "Username": "...", "Password": "..." }
    ///
    /// var builder = Host.CreateApplicationBuilder(args);
    /// builder.AddMessaging();
    /// </code>
    /// </example>
    public static IHostApplicationBuilder AddMessaging(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IMessageBus, RabbitMqBus>();
        builder.Services
            .AddSingleton<IMessagingIdentifier, MessagingIdentifier>()
            .AddOptions<MessagingOptions>().Bind(builder.Configuration.GetSection(MessagingOptions.SectionName));
        builder.Services
            .AddOptions<BusEnvelopeJsonOptions>().Bind(builder.Configuration.GetSection(BusEnvelopeJsonOptions.SectionName));

        return builder;
    }
}
