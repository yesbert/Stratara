using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Orleans.Singleton;
using Stratara.Abstractions.Singleton;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of singleton work on the silo.</summary>
public static class OrleansSingletonWorkServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TWork"/> to run once per cluster on its period. Every silo that
    /// registers it asks its grain to run once the silo is active, whatever order the silo and this call
    /// were registered in; the grain runs on exactly one of them, and never on a silo that did not register
    /// the work. Its settings are validated when the host starts.
    /// </summary>
    /// <typeparam name="TWork">The work.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional runner settings.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddStrataraSingletonWork&lt;OutboxDrainWork&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraSingletonWork<TWork>(this IServiceCollection services, Action<SingletonWorkOptions>? configure = null)
        where TWork : class, ISingletonWork
    {
        var options = services.AddOptions<SingletonWorkOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddOptions<OutboxDrainOptions>();
        Stratara.Orleans.Hosting.OrleansOptionsValidator.Register<SingletonWorkOptions>(services);
        Stratara.Orleans.Hosting.OrleansOptionsValidator.Register<OutboxDrainOptions>(services);
        services.AddScoped<ISingletonWork, TWork>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILifecycleParticipant<global::Orleans.Runtime.ISiloLifecycle>, SingletonWorkStarter>());
        Stratara.Orleans.Hosting.DurableDirectoryCheck.Register(services);
        return services;
    }
}
