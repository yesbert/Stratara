using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.EventSourcing;
using Stratara.Sagas.Abstractions;
using Stratara.Sagas.Services;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Service-collection extensions for the Stratara saga runtime.</summary>
public static class SagaServiceCollectionExtensions
{
    /// <summary>
    /// Registers the saga runtime services (<see cref="ISagaManager"/>, <see cref="ISagaHandler"/>,
    /// <see cref="ISagaMethodInvoker"/>) and the hosted <c>SagaWorker</c>; binds <c>SagaOptions</c> from
    /// the <c>Sagas</c> configuration section.
    /// </summary>
    /// <remarks>Use together with <see cref="AddSagasFromAssemblyContaining{T}"/> to discover concrete saga implementations.</remarks>
    /// <example>
    /// Binds <c>SagaOptions</c> from the <c>Sagas</c> section and consumes the event-bundle saga
    /// subscription. Register the sagas separately:
    /// <code>
    /// services.AddSagasFromAssemblyContaining&lt;TransferSaga&gt;();
    /// services.AddSagaWorker(configuration);
    /// </code>
    /// </example>
    public static IServiceCollection AddSagaWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSagaHandling(configuration);
        services.AddHostedService<SagaWorker>();
        return services;
    }

    /// <summary>
    /// Registers the saga runtime services (<see cref="ISagaManager"/>, <see cref="ISagaHandler"/>,
    /// <see cref="ISagaMethodInvoker"/>) without the hosted <c>SagaWorker</c>, and binds <c>SagaOptions</c> from
    /// the <c>Sagas</c> configuration section. For a host whose sagas are fed by something other than the bus,
    /// such as the Orleans execution model's store readers. Calling it more than once registers each service once.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root used to bind <c>SagaOptions</c>.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddSagasFromAssemblyContaining&lt;TransferSaga&gt;();
    /// services.AddSagaHandling(configuration);
    /// </code>
    /// </example>
    public static IServiceCollection AddSagaHandling(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<ISagaManager, SagaManager>();
        services.TryAddScoped<ISagaHandler, SagaHandler>();
        services.TryAddScoped<ISagaMethodInvoker, SagaMethodInvoker>();

        services.AddOptions<SagaOptions>()
            .Bind(configuration.GetSection(SagaOptions.SectionName));
        return services;
    }

    /// <summary>
    /// Scans the assembly that contains <typeparamref name="T"/> for concrete <see cref="ISaga"/>
    /// implementations and registers each as a scoped <see cref="ISaga"/>.
    /// </summary>
    /// <typeparam name="T">Any type from the assembly to scan.</typeparam>
    /// <example>
    /// Discover and register every <see cref="ISaga"/> in the saga assembly:
    /// <code>
    /// public interface ISagasMarker { }
    ///
    /// builder.Services.AddSagasFromAssemblyContaining&lt;ISagasMarker&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddSagasFromAssemblyContaining<T>(this IServiceCollection services)
    {
        var resolver = TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver(services);
        var assembly = typeof(T).Assembly;

        foreach (var type in assembly.GetTypes()
                     .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(ISaga).IsAssignableFrom(t)))
        {
            services.AddScoped(typeof(ISaga), type);
            RegisterHandledEventTypes(type, resolver);
        }

        return services;
    }

    [SuppressMessage("Major Code Smell", "S3011:Reflection should not be used to increase accessibility of classes, methods, or fields",
        Justification = "Saga handlers may declare HandleAsync as a non-public member; the DI scan must reflect over both " +
                        "to populate the trusted-type allowlist with declared event types at startup.")]
    private static void RegisterHandledEventTypes(Type sagaType, Stratara.Abstractions.Reflections.TrustedTypeResolver resolver)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var method in sagaType.GetMethods(flags)
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
