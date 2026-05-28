using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.EventSourcing;
using Stratara.Projections.Abstractions;
using Stratara.Projections.Services;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Dependency-injection extension methods that wire the Stratara projection runtime (manager, handler,
/// method invoker, worker, replay worker) and discover <see cref="IProjection"/> implementations by
/// assembly scan.
/// </summary>
public static class ProjectionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the projection runtime — <see cref="ProjectionManager"/>, <see cref="ProjectionHandler"/>,
    /// <see cref="ProjectionMethodInvoker"/>, <see cref="ProjectionWorker"/>, and
    /// <see cref="ProjectionReplayWorker"/> — and binds <see cref="ProjectionOptions"/> from configuration.
    /// </summary>
    /// <param name="services">The service collection to register against.</param>
    /// <param name="configuration">The configuration root used to bind <see cref="ProjectionOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddProjectionWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IProjectionManager, ProjectionManager>();
        services.AddScoped<IProjectionHandler, ProjectionHandler>();
        services.AddScoped<IProjectionMethodInvoker, ProjectionMethodInvoker>();
        services.AddHostedService<ProjectionWorker>();
        services.AddHostedService<ProjectionReplayWorker>();

        services.AddOptions<ProjectionOptions>()
            .Bind(configuration.GetSection(ProjectionOptions.SectionName));
        return services;
    }

    /// <summary>
    /// Scans the assembly containing <typeparamref name="T"/> and registers every concrete
    /// <see cref="IProjection"/> implementation it finds as a scoped service.
    /// </summary>
    /// <typeparam name="T">A marker type that lives in the assembly to scan (typically a per-project <c>IMarker</c> interface).</typeparam>
    /// <param name="services">The service collection to register against.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <example>
    /// Discover and register every <see cref="IProjection"/> in the projections assembly:
    /// <code>
    /// public interface IProjectionsMarker { }
    ///
    /// builder.Services.AddProjectionsFromAssemblyContaining&lt;IProjectionsMarker&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddProjectionsFromAssemblyContaining<T>(this IServiceCollection services)
    {
        var resolver = TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver(services);
        var assembly = typeof(T).Assembly;

        foreach (var type in assembly.GetTypes()
                     .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IProjection).IsAssignableFrom(t)))
        {
            services.AddScoped(typeof(IProjection), type);
            RegisterHandledEventTypes(type, resolver);
        }

        return services;
    }

    [SuppressMessage("Major Code Smell", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields",
        Justification = "Projection handlers may declare HandleAsync as a non-public member; the DI scan must reflect over both " +
                        "to populate the trusted-type allowlist with declared event types at startup.")]
    private static void RegisterHandledEventTypes(Type projectionType, Stratara.Abstractions.Reflections.TrustedTypeResolver resolver)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var method in projectionType.GetMethods(flags)
                     .Where(m => m.Name == "HandleAsync" && m.ReturnType == typeof(Task)))
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 2)
            {
                continue;
            }
            var eventParamType = parameters[0].ParameterType;
            if (eventParamType.IsGenericType && eventParamType.GetGenericTypeDefinition() == typeof(IEvent<>))
            {
                eventParamType = eventParamType.GetGenericArguments()[0];
            }
            resolver.Register(eventParamType);
        }
    }
}
