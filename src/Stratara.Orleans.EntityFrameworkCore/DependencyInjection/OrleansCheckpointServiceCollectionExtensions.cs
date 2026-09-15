using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stratara.Abstractions.Projections;
using Stratara.Orleans.EntityFrameworkCore.Projections;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the projection checkpoint store in a read context.</summary>
public static class OrleansCheckpointServiceCollectionExtensions
{
    /// <summary>
    /// Keeps the checkpoints of the store-reading projections and sagas in
    /// <typeparamref name="TReadContext"/>. Binds no configuration. Requires a registered
    /// <see cref="IDbContextFactory{TContext}"/> for a read context derived from the framework's read
    /// context, which declares the checkpoint table. A checkpoint store registered before this call is kept.
    /// </summary>
    /// <typeparam name="TReadContext">The read context that holds the checkpoint table.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddStrataraProjectionGrains()
    ///     .AddStrataraProjectionCheckpoints&lt;AppReadDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraProjectionCheckpoints<TReadContext>(this IServiceCollection services)
        where TReadContext : DbContext
    {
        services.TryAddScoped<IProjectionCheckpointStore, ProjectionCheckpointStore<TReadContext>>();
        return services;
    }
}
