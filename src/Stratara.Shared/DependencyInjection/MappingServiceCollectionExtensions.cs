using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Shared.EventSourcing.Mapping;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Dependency-injection helpers that register Stratara's mapping primitives (event-stream entry
/// to <see cref="IEvent"/> conversion) into an <see cref="IServiceCollection"/>.
/// </summary>
public static class MappingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the singleton <see cref="IEventMapperFactory"/> implementation used by the
    /// event-sourcing stack to materialize typed <see cref="IEvent"/> instances from persisted
    /// <c>EventStreamEntry</c> / <c>EventMessage</c> rows.
    /// </summary>
    /// <param name="services">The DI container to extend.</param>
    /// <returns>The same <paramref name="services"/> instance to allow fluent chaining.</returns>
    public static IServiceCollection AddMapping(this IServiceCollection services)
    {
        services.AddSingleton<IEventMapperFactory, EventMapperFactory>();
        return services;
    }
}
