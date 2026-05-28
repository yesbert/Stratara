using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Stratara.Mediator;
using Stratara.Mediator.Authorization;
using Stratara.Abstractions.Mediator;
using Stratara.Abstractions.Reflections;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI extensions that register the Stratara mediator + pipeline behaviors + handler discovery.
/// </summary>
public static class MediatorServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="IMediator"/> as a scoped service.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent — calling multiple times appends duplicate registrations; DI resolves the last
    /// one. Use <see cref="AuthorizationServiceCollectionExtensions.AddAuthorizingMediator{T}"/>
    /// instead if you want the authorizing decorator chain.
    /// </para>
    /// <para>
    /// This method also wires a startup-time validator that fails fast if the host registers
    /// <c>[RequireRole]</c>-annotated request types without also pointing <see cref="IMediator"/>
    /// at an <see cref="IAuthorizingMediator"/>. Custom decorators that wrap
    /// <see cref="AuthorizingMediator"/> must implement
    /// <see cref="IAuthorizingMediator"/> on their outermost layer so the validator
    /// recognises them.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Register the mediator alongside handlers discovered in the host's assembly:
    /// <code>
    /// var builder = WebApplication.CreateBuilder(args);
    ///
    /// builder.Services
    ///     .AddMediator()
    ///     .AddCommandHandlersFromAssemblyContaining&lt;Program&gt;()
    ///     .AddQueryHandlersFromAssemblyContaining&lt;Program&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddMediator(this IServiceCollection services)
    {
        services.AddScoped<IMediator, Mediator>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, AuthorizationStartupValidator>());
        return services;
    }

    /// <summary>
    /// Register an open-generic <see cref="IPipelineBehavior{TRequest,TResult}"/> implementation.
    /// </summary>
    /// <remarks>
    /// The provided type must be a two-parameter open generic
    /// (e.g. <c>typeof(LoggingBehavior&lt;,&gt;)</c>). Behaviors are resolved per-request in DI
    /// registration order — first registered runs outermost.
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="openGenericBehaviorType">An open generic type definition with two type parameters.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="openGenericBehaviorType"/> is null.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="openGenericBehaviorType"/> is not an open generic with two type parameters.</exception>
    public static IServiceCollection AddPipelineBehaviorWithResult(this IServiceCollection services, Type openGenericBehaviorType)
    {
        ArgumentNullException.ThrowIfNull(openGenericBehaviorType);
        if (!openGenericBehaviorType.IsGenericTypeDefinition || openGenericBehaviorType.GetGenericArguments().Length != 2)
        {
            throw new ArgumentException(
                $"'{openGenericBehaviorType.Name}' must be an open generic type definition with two type parameters (TRequest, TResult).",
                nameof(openGenericBehaviorType));
        }

        services.AddScoped(typeof(IPipelineBehavior<,>), openGenericBehaviorType);
        return services;
    }

    /// <summary>
    /// Register an open-generic <see cref="IPipelineBehavior{TRequest}"/> implementation
    /// for void commands.
    /// </summary>
    /// <remarks>
    /// The provided type must be a one-parameter open generic
    /// (e.g. <c>typeof(LoggingBehavior&lt;&gt;)</c>). Behaviors are resolved per-request in DI
    /// registration order — first registered runs outermost.
    /// </remarks>
    /// <param name="services">The service collection to mutate.</param>
    /// <param name="openGenericBehaviorType">An open generic type definition with one type parameter.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="openGenericBehaviorType"/> is null.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="openGenericBehaviorType"/> is not an open generic with one type parameter.</exception>
    public static IServiceCollection AddPipelineBehavior(this IServiceCollection services, Type openGenericBehaviorType)
    {
        ArgumentNullException.ThrowIfNull(openGenericBehaviorType);
        if (!openGenericBehaviorType.IsGenericTypeDefinition || openGenericBehaviorType.GetGenericArguments().Length != 1)
        {
            throw new ArgumentException(
                $"'{openGenericBehaviorType.Name}' must be an open generic type definition with one type parameter (TRequest).",
                nameof(openGenericBehaviorType));
        }

        services.AddScoped(typeof(IPipelineBehavior<>), openGenericBehaviorType);
        return services;
    }

    /// <summary>
    /// Scan the assembly containing <typeparamref name="T"/> and register every concrete
    /// <see cref="ICommandHandler{TRequest}"/> implementation as a scoped service.
    /// </summary>
    /// <typeparam name="T">A marker type from the assembly to scan. Typically <c>Program</c> or the application's marker interface.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Register every command handler in the host's assembly via a marker interface
    /// so domain assemblies are scanned without referencing concrete types here:
    /// <code>
    /// public interface IAppMarker { }
    ///
    /// builder.Services.AddCommandHandlersFromAssemblyContaining&lt;IAppMarker&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddCommandHandlersFromAssemblyContaining<T>(this IServiceCollection services)
    {
        var resolver = TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver(services);
        var assembly = typeof(T).Assembly;

        foreach (var type in assembly
                     .GetTypes()
                     .Where(t => t is { IsAbstract: false, IsInterface: false }))
        {
            var interfaces = type.GetInterfaces();
            foreach (var @interface in interfaces)
            {
                switch (@interface.IsGenericType)
                {
                    case true when @interface.GetGenericTypeDefinition() == typeof(ICommandHandler<>):
                        services.AddScoped(@interface, type);
                        resolver.Register(@interface.GetGenericArguments()[0]);
                        break;
                }
            }
        }

        return services;
    }

    /// <summary>
    /// Scan the assembly containing <typeparamref name="T"/> and register every concrete
    /// <see cref="IQueryHandler{TRequest,TResult}"/> implementation as a scoped service.
    /// </summary>
    /// <typeparam name="T">A marker type from the assembly to scan. Typically <c>Program</c> or the application's marker interface.</typeparam>
    /// <param name="services">The service collection to mutate.</param>
    /// <returns>The same service collection, to enable chaining.</returns>
    /// <example>
    /// Register every query handler in the host's assembly. The same call also picks up
    /// handlers for <c>ICommand&lt;TResult&gt;</c> because both share the
    /// <c>IQueryHandler&lt;TRequest, TResult&gt;</c> contract.
    /// <code>
    /// builder.Services.AddQueryHandlersFromAssemblyContaining&lt;IAppMarker&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddQueryHandlersFromAssemblyContaining<T>(this IServiceCollection services)
    {
        var assembly = typeof(T).Assembly;

        foreach (var type in assembly
                     .GetTypes()
                     .Where(t => t is { IsAbstract: false, IsInterface: false }))
        {
            var interfaces = type.GetInterfaces();
            foreach (var @interface in interfaces)
            {
                switch (@interface.IsGenericType)
                {
                    case true when @interface.GetGenericTypeDefinition() == typeof(IQueryHandler<,>):
                        services.AddScoped(@interface, type);
                        break;
                }
            }
        }

        return services;
    }
}
