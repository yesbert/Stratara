using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.Messaging;
using Stratara.Outbox.AzureServiceBus.Messaging;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service-collection extensions for wiring Azure Service Bus as the Stratara
/// <see cref="IMessageBus"/> implementation.
/// </summary>
public static class AzureServiceBusServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Stratara Azure Service Bus message bus using a SAS-token
    /// connection string. The connection string typically lives in
    /// <c>ConnectionStrings:ServiceBus</c> and is suitable for local development.
    /// For production, prefer
    /// <see cref="AddAzureServiceBusWithManagedIdentity(IServiceCollection, string, TokenCredential?)"/>
    /// so the broker is reached via Azure-AD-issued tokens instead of long-lived SAS keys.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">Azure Service Bus connection string (SAS).</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is <c>null</c> or empty.</exception>
    public static IServiceCollection AddAzureServiceBus(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        services.TryAddSingleton(_ => new ServiceBusClient(connectionString));
        services.TryAddSingleton<IMessageBus, AzureServiceBusBus>();

        return services;
    }

    /// <summary>
    /// Registers the Stratara Azure Service Bus message bus using Azure-AD authentication
    /// (Managed Identity by default, or a caller-supplied <see cref="TokenCredential"/>).
    /// **Preferred path for production** — SAS tokens leak through configuration files and
    /// secret stores with a much wider exposure surface than AAD-issued tokens; Managed
    /// Identity also rotates and revokes automatically (Round-3-Audit Finding R3-Sec-007).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="fullyQualifiedNamespace">The Azure Service Bus namespace FQDN, e.g. <c>my-bus.servicebus.windows.net</c>.</param>
    /// <param name="credential">Optional <see cref="TokenCredential"/>; defaults to <see cref="DefaultAzureCredential"/> (Managed Identity → environment → CLI chain).</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="fullyQualifiedNamespace"/> is <c>null</c> or empty.</exception>
    public static IServiceCollection AddAzureServiceBusWithManagedIdentity(
        this IServiceCollection services,
        string fullyQualifiedNamespace,
        TokenCredential? credential = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullyQualifiedNamespace);

        var tokenCredential = credential ?? new DefaultAzureCredential();
        services.TryAddSingleton(_ => new ServiceBusClient(fullyQualifiedNamespace, tokenCredential));
        services.TryAddSingleton<IMessageBus, AzureServiceBusBus>();

        return services;
    }
}
