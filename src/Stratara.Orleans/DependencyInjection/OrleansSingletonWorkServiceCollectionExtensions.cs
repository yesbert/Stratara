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
    /// the work. A run that throws is logged and the work runs again at its next period. Its settings are
    /// validated when the host starts; a second call for the same work registers nothing more.
    /// </summary>
    /// <remarks>
    /// The silo publishes the name of every work it registers when it starts, and without a registered name it
    /// constructs the work then to read it — before the silo is active and before any hosted service registered
    /// after the silo has run. A work whose construction needs the running host is registered with
    /// <see cref="AddStrataraSingletonWork{TWork}(IServiceCollection, string, Action{SingletonWorkOptions})"/>.
    /// </remarks>
    /// <typeparam name="TWork">The work.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional runner settings.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddStrataraSingletonWork&lt;NightlyCleanupWork&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraSingletonWork<TWork>(this IServiceCollection services, Action<SingletonWorkOptions>? configure = null)
        where TWork : class, ISingletonWork =>
        Add<TWork>(services, name: null, configure);

    /// <summary>
    /// Registers <typeparamref name="TWork"/> to run once per cluster on its period under <paramref name="name"/>, the
    /// <see cref="ISingletonWork.Name"/> it publishes under. The silo publishes the registered name without
    /// constructing the work, so the work is first constructed when the silo is active; a work whose
    /// <see cref="ISingletonWork.Name"/> differs from <paramref name="name"/> fails the silo's start with a message
    /// naming both. Otherwise it behaves as
    /// <see cref="AddStrataraSingletonWork{TWork}(IServiceCollection, Action{SingletonWorkOptions})"/>.
    /// </summary>
    /// <typeparam name="TWork">The work.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The name the work publishes under, equal to its <see cref="ISingletonWork.Name"/>.</param>
    /// <param name="configure">Optional runner settings.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/>, empty or white space.</exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="TWork"/> is already registered under another name.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddStrataraSingletonWork&lt;OutboxDrainWork&gt;(OutboxDrainWork.WorkName);
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraSingletonWork<TWork>(this IServiceCollection services, string name, Action<SingletonWorkOptions>? configure = null)
        where TWork : class, ISingletonWork
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Add<TWork>(services, name, configure);
    }

    private static IServiceCollection Add<TWork>(IServiceCollection services, string? name, Action<SingletonWorkOptions>? configure)
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
        if (!services.Any(d => d.ServiceType == typeof(ISingletonWork) && d.ImplementationType == typeof(TWork)))
        {
            services.AddScoped<ISingletonWork, TWork>();
        }

        SingletonWorkRegistrations.Of(services).Add(typeof(TWork), name);
        services.TryAddSingleton<Stratara.Orleans.Aggregates.ReplaySuspensionTracker>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILifecycleParticipant<global::Orleans.Runtime.ISiloLifecycle>, SingletonWorkStarter>());
        Stratara.Orleans.Hosting.DurableDirectoryCheck.Register(services);
        return services;
    }
}
