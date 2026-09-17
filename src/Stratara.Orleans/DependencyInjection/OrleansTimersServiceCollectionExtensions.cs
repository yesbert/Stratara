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
    /// the silo with a reminder service of its own choosing, and supplies one <see cref="ITimerOwners"/>
    /// and one <see cref="ITimerHandler"/>, before or after this call; the execution model's own owners, such
    /// as stateful processes, are served by ports of their own beside them. The host fails at start when its
    /// ports do not compose — two owner checks, one without a handler, or processes without their timers —
    /// and when <see cref="DurableTimerOptions.RetryPeriod"/> is below the runtime's minimum reminder period.
    /// A silo that registers an <see cref="ITimerOwners"/> publishes the timer role: timer owners are placed only
    /// on silos that have the ports, so a silo that registers the timers only to register them, with its owners
    /// elsewhere, hosts no timer owner.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional settings; binds nothing from configuration on its own.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// var cs = builder.Configuration.GetConnectionString("orleans")!;
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

        Stratara.Orleans.Hosting.OrleansOptionsValidator.Register<DurableTimerOptions>(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDurableTimers, DurableTimers>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, TimerPortsStartupCheck>());
        Stratara.Orleans.Hosting.DurableDirectoryCheck.Register(services);
        Stratara.Orleans.Hosting.RolePlacement.Publish(services, Stratara.Orleans.Hosting.ExecutionRole.Timers);
        return services;
    }
}
