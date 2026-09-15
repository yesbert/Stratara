using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Orleans.Timers;
using Stratara.Abstractions.Timers;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the Orleans-backed durable timers.</summary>
public static class OrleansTimersServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IDurableTimers"/> over the silo's reminder service. The host registers
    /// the silo with a reminder service of its own choosing, and supplies <see cref="ITimerOwners"/>
    /// and <see cref="ITimerHandler"/>; without them the first timer to fire fails to resolve them.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional settings; binds nothing from configuration on its own.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.UseOrleans(silo => silo.UseAdoNetReminderService(o => { o.Invariant = "Npgsql"; o.ConnectionString = cs; }));
    /// builder.Services
    ///     .AddStrataraDurableTimers()
    ///     .AddScoped&lt;ITimerOwners, OrderTimerOwners&gt;()
    ///     .AddScoped&lt;ITimerHandler, OrderTimerHandler&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraDurableTimers(this IServiceCollection services, Action<DurableTimerOptions>? configure = null)
    {
        var options = services.AddOptions<DurableTimerOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDurableTimers, DurableTimers>();
        return services;
    }
}
