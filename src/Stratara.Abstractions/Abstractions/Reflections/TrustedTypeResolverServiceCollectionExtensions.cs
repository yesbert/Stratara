using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.Domain;
using Stratara.Abstractions.Reflections;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions that register the <see cref="ITrustedTypeResolver"/> singleton and populate it with
/// command / event / aggregate types discovered by assembly scanning.
/// </summary>
/// <remarks>
/// The resolver is the framework-wide allowlist consulted whenever a persisted type-name string is
/// converted back to a runtime <see cref="Type"/>. Code paths that previously called
/// <see cref="Type.GetType(string)"/> on attacker-controlled input (RabbitMQ / Service Bus envelopes,
/// event-stream rows, snapshot rows) now resolve through the allowlist instead.
/// </remarks>
public static class TrustedTypeResolverServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="ITrustedTypeResolver"/> as a singleton if no implementation has been
    /// registered yet.
    /// </summary>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddTrustedTypeResolver(this IServiceCollection services)
    {
        GetOrAddResolver(services);
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="T"/> in the trusted-type allowlist. Use for types produced but
    /// never directly handled (e.g. aggregate snapshot types whose aggregates have no projection / saga
    /// to anchor an automatic scan).
    /// </summary>
    /// <typeparam name="T">The type to add to the allowlist.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddTrustedType<T>(this IServiceCollection services)
    {
        var resolver = GetOrAddResolver(services);
        resolver.Register(typeof(T));
        return services;
    }

    /// <summary>
    /// Scans the assembly containing <typeparamref name="T"/> for concrete <see cref="IAggregate"/>
    /// implementations and registers each in the trusted-type resolver — together with every
    /// event type consumed by the aggregate's <c>Apply(...)</c> methods — so both
    /// <c>SnapshotService</c> can materialize the aggregate and <c>IAggregationService</c> can
    /// replay its event stream without further <c>AddTrustedType&lt;TEvent&gt;()</c> calls.
    /// </summary>
    /// <typeparam name="T">A marker type from the assembly to scan.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// For each discovered aggregate, every public instance method named <c>Apply</c> that takes
    /// a single parameter contributes its parameter type to the allowlist. This mirrors the
    /// event-sourcing convention used throughout the framework (see
    /// <see cref="Stratara.Abstractions.Domain.IAggregate"/>).
    /// </remarks>
    public static IServiceCollection AddAggregatesFromAssemblyContaining<T>(this IServiceCollection services)
    {
        var resolver = GetOrAddResolver(services);
        var assembly = typeof(T).Assembly;
        foreach (var aggregateType in assembly.GetTypes()
                     .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IAggregate).IsAssignableFrom(t)))
        {
            resolver.Register(aggregateType);
            foreach (var applyMethod in aggregateType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => m.Name == "Apply"))
            {
                var parameters = applyMethod.GetParameters();
                if (parameters.Length != 1)
                {
                    continue;
                }
                resolver.Register(parameters[0].ParameterType);
            }
        }
        return services;
    }

    /// <summary>
    /// Internal helper used by other scan extensions (<c>AddCommandHandlersFromAssemblyContaining</c>,
    /// <c>AddProjectionsFromAssemblyContaining</c>, <c>AddSagasFromAssemblyContaining</c>) to retrieve
    /// the shared <see cref="TrustedTypeResolver"/> singleton during <c>ConfigureServices</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The shared resolver instance, created and registered on first call.</returns>
    public static TrustedTypeResolver GetOrAddResolver(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(s =>
            s.ServiceType == typeof(ITrustedTypeResolver) && s.ImplementationInstance is TrustedTypeResolver);
        if (existing?.ImplementationInstance is TrustedTypeResolver registered)
        {
            return registered;
        }

        var resolver = new TrustedTypeResolver();
        services.RemoveAll<ITrustedTypeResolver>();
        services.AddSingleton<ITrustedTypeResolver>(resolver);
        return resolver;
    }
}
