using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Stratara.Orleans.EntityFrameworkCore.Hosting;
using Stratara.Orleans.Hosting;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the execution model's reset.</summary>
public static class OrleansResetServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IExecutionModelReset"/> for a deployment whose runtime keeps its reminders and
    /// membership in PostgreSQL and whose checkpoints live in <typeparamref name="TReadContext"/>. The reminders
    /// are cleared for the host's service and the membership for its cluster, as the silo's cluster options name
    /// them; the checkpoints are cleared in full. The grain directory is cleared by
    /// <paramref name="clearDirectory"/>, because its backend is the host's choice. Binds no configuration.
    /// Requires a registered <see cref="IDbContextFactory{TContext}"/> for the read context. A reset registered
    /// before this call is kept.
    /// </summary>
    /// <typeparam name="TReadContext">The read context that holds the checkpoint table.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="runtimeConnectionString">The database of the runtime's reminder and membership tables.</param>
    /// <param name="clearDirectory">Removes the directory entries of the host's cluster and returns how many it removed.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="runtimeConnectionString"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="clearDirectory"/> is <see langword="null"/>.</exception>
    /// <example>
    /// Run it while no silo of the cluster runs:
    /// <code>
    /// var orleansConnectionString = builder.Configuration.GetConnectionString("orleans")!;
    /// builder.Services.AddStrataraExecutionModelReset&lt;AppReadDbContext&gt;(
    ///     orleansConnectionString,
    ///     async (services, cancellationToken) =>
    ///     {
    ///         // With the Redis directory: remove the cluster's keys and report how many.
    ///         var redis = services.GetRequiredService&lt;StackExchange.Redis.IConnectionMultiplexer&gt;();
    ///         var keys = redis.GetServer(redis.GetEndPoints()[0]).Keys(pattern: "*my-cluster*").ToArray();
    ///         return keys.Length == 0 ? 0 : await redis.GetDatabase().KeyDeleteAsync(keys);
    ///     });
    ///
    /// var report = await app.Services.GetRequiredService&lt;IExecutionModelReset&gt;().ResetAsync();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraExecutionModelReset<TReadContext>(
        this IServiceCollection services,
        string runtimeConnectionString,
        Func<IServiceProvider, CancellationToken, Task<long>> clearDirectory)
        where TReadContext : DbContext
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeConnectionString);
        ArgumentNullException.ThrowIfNull(clearDirectory);

        services.AddOptions<ClusterOptions>();
        services.TryAddSingleton(new ExecutionModelResetSettings(runtimeConnectionString, clearDirectory));
        services.TryAddScoped<IExecutionModelReset, ExecutionModelReset<TReadContext>>();
        return services;
    }
}
