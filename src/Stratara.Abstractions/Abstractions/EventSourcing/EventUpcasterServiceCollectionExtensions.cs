using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.EventSourcing;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions that register <see cref="IEventUpcaster"/> implementations and the
/// <see cref="IEventUpcasterPipeline"/> that the event-mapping layer consumes on read.
/// </summary>
/// <remarks>
/// The default <see cref="EventUpcasterPipeline"/> is registered lazily on the first
/// <c>AddEventUpcaster</c> call (and by the event-sourcing mapping wiring), so a host that registers no
/// upcasters still resolves a transparent pass-through pipeline.
/// </remarks>
public static class EventUpcasterServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TUpcaster"/> as an <see cref="IEventUpcaster"/> and ensures the
    /// default <see cref="IEventUpcasterPipeline"/> is present.
    /// </summary>
    /// <typeparam name="TUpcaster">The upcaster implementation to register.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddEventUpcaster<TUpcaster>(this IServiceCollection services)
        where TUpcaster : class, IEventUpcaster
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddEventUpcasterPipeline();
        services.AddSingleton<IEventUpcaster, TUpcaster>();
        return services;
    }

    /// <summary>
    /// Registers an already-constructed <see cref="IEventUpcaster"/> and ensures the default
    /// <see cref="IEventUpcasterPipeline"/> is present.
    /// </summary>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="upcaster">The upcaster instance to register.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddEventUpcaster(this IServiceCollection services, IEventUpcaster upcaster)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(upcaster);
        services.AddEventUpcasterPipeline();
        services.AddSingleton(upcaster);
        return services;
    }

    /// <summary>
    /// Registers the default <see cref="IEventUpcasterPipeline"/> if no implementation has been
    /// registered yet. Idempotent; called automatically by every <c>AddEventUpcaster</c> overload and by
    /// the event-mapping wiring.
    /// </summary>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddEventUpcasterPipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IEventUpcasterPipeline, EventUpcasterPipeline>();
        return services;
    }
}
