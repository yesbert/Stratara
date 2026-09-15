using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.CommitOrder;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.Orleans.EntityFrameworkCore.CommitOrder;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the portable commit-order reader and the check that the store is positioned for it.</summary>
public static class OrleansCommitOrderServiceCollectionExtensions
{
    /// <summary>
    /// Reads the event store in commit order through the per-partition counter, on any relational
    /// provider. Binds no configuration; the partition count comes from <c>CommitOrderOptions</c>. The
    /// host fails at start while the store holds an entry without a position — entries written before
    /// the counter was adopted — with a message naming <see cref="PartitionCounterBackfill"/>. Requires a
    /// registered <see cref="IDbContextFactory{TContext}"/> for the write context, and the write context
    /// must add <see cref="PartitionCounterInterceptor"/> so every append is positioned. Register it
    /// before the execution model's projection and saga registrations.
    /// </summary>
    /// <typeparam name="TWriteContext">A write context derived from the framework's write context.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddNpgsqlWriteDbContextFactory&lt;AppWriteDbContext&gt;()
    ///     .AddStrataraPortableCounterReader&lt;AppWriteDbContext&gt;()
    ///     .AddStrataraProjectionCheckpoints&lt;AppReadDbContext&gt;()
    ///     .AddStrataraProjectionGrains();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraPortableCounterReader<TWriteContext>(this IServiceCollection services)
        where TWriteContext : DbContext, IWriteDbContext
    {
        services.AddOptions<Stratara.Orleans.CommitOrder.CommitOrderOptions>();
        services.TryAddScoped<ICommittedPositionReader, PortableCounterReader<TWriteContext>>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PortableCounterStartupCheck<TWriteContext>>());
        return services;
    }
}
