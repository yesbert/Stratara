using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.ReadStore.Replay;
using Stratara.Projections.Abstractions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the PostgreSQL preservation that lets a failed replay restore the read models.</summary>
public static class ReadModelRestoreServiceCollectionExtensions
{
    /// <summary>
    /// Keeps the read models a replay is about to empty, so a replay that fails — or whose host stops — leaves the read
    /// store as it was before the replay began, instead of empty or half rebuilt. PostgreSQL only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before a replay empties anything, every table <typeparamref name="TReadContext"/> maps — the read models, the
    /// projection checkpoints and the record of forgotten tenants among them — is copied into a schema of its own
    /// (<see cref="ReadModelRestoreOptions.Schema"/>) as one consistent snapshot. A replay that fails writes the copy back
    /// in one transaction; one that succeeds drops it; one whose host stops leaves it, and the next host that starts
    /// writes it back. Readers still see the rebuild in progress while it runs.
    /// </para>
    /// <para>
    /// The copy costs the time to read the read store once more before the replay starts, and the disk to hold it twice
    /// while the replay runs. Tables the host's view truncator empties but the context does not map are covered through
    /// <see cref="ReadModelRestoreOptions.AdditionalTables"/>; mapped tables a replay leaves alone are left out through
    /// <see cref="ReadModelRestoreOptions.ExcludedTables"/>. Settings are read from the <c>ProjectionReplay:Restore</c>
    /// section. Requires a registered <see cref="IDbContextFactory{TContext}"/> for <typeparamref name="TReadContext"/>;
    /// a preservation registered before this call is kept.
    /// </para>
    /// </remarks>
    /// <typeparam name="TReadContext">The read context whose tables a replay empties.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Adjusts the settings after the configuration section is applied.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddNpgsqlReadDbContextFactory&lt;AppReadDbContext&gt;()
    ///     .AddReadModelRestore&lt;AppReadDbContext&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddReadModelRestore<TReadContext>(
        this IServiceCollection services,
        Action<ReadModelRestoreOptions>? configure = null)
        where TReadContext : DbContext, IReadDbContext
    {
        services.AddOptions<ReadModelRestoreOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<ReadModelRestoreOptions>, ReadModelRestoreOptionsBinding>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ReadModelRestoreOptions>, ReadModelRestoreOptionsBinding>());
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddScoped<IReadModelPreservation, NpgsqlReadModelPreservation<TReadContext>>();
        return services;
    }
}
