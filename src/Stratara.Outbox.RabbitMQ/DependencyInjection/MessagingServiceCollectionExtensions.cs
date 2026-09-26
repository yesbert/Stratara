using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// <see cref="MessagingOptions"/> from the <c>Messaging</c> configuration section,
    /// <see cref="BusEnvelopeJsonOptions"/> from <c>BusEnvelopeJson</c> and
    /// <see cref="MessageRetryOptions"/> from <c>MessageRetry</c>, the retry bounds and the prefetch bound validated
    /// at start-up so that a bound of zero is refused before a message is consumed. When the host stops, it waits —
    /// within the host's shutdown timeout — for the subscriptions that stopped with it to let their running handlers
    /// settle, before anything is disposed.
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
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RabbitMqSubscriptionsDrain>());
        builder.Services
            .AddSingleton<IMessagingIdentifier, MessagingIdentifier>()
            .AddOptions<MessagingOptions>().Bind(builder.Configuration.GetSection(MessagingOptions.SectionName))
            .Validate(options => options.PrefetchCount is >= 1 and <= ushort.MaxValue,
                "Messaging:PrefetchCount must be between 1 and 65535: a subscription holds that many messages at most, and none would take none.")
            .ValidateOnStart();
        builder.Services
            .AddOptions<BusEnvelopeJsonOptions>().Bind(builder.Configuration.GetSection(BusEnvelopeJsonOptions.SectionName));
        builder.Services
            .AddOptions<MessageRetryOptions>().Bind(builder.Configuration.GetSection(MessageRetryOptions.SectionName))
            .Validate(MessageRetryOptionsValidation.IsValid, MessageRetryOptionsValidation.Message)
            .ValidateOnStart();

        return builder;
    }
}
