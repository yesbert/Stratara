using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.CommitOrder;
using Stratara.EventSourcing.EntityFrameworkCore.WriteStore.EventSourcing.Configurations;

namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore;

/// <summary>
/// Base write-store DbContext that consumer DbContexts derive from. Hosts the event-sourcing
/// tables — <c>event_stream_entry</c>, <c>snapshot</c>, <c>command_log_entry</c>, and
/// <c>outbox_entry</c> — that the command, hashing, and outbox workers persist into.
/// </summary>
/// <typeparam name="TContext">The concrete derived DbContext type (used for <see cref="DbContextOptions{TContext}"/> binding).</typeparam>
/// <remarks>
/// Filters <c>ApplyConfigurationsFromAssembly</c> by the <c>Stratara.EventSourcing.EntityFrameworkCore.WriteStore</c> namespace so
/// the read-store and identity-store <c>IEntityTypeConfiguration&lt;&gt;</c> implementations
/// co-hosted in the same assembly do not leak into the write model. Missing the namespace
/// predicate yields <c>PendingModelChangesWarning</c> at runtime — a regression that escapes
/// unit tests and only surfaces against a real Postgres in the consumer's E2E pipeline.
/// <para>
/// The model also declares what reading the event stream in commit order needs: a per-partition
/// position on every entry with its counter table, and on PostgreSQL the id of the inserting
/// transaction. A host that does not read in commit order migrates these and never fills them.
/// </para>
/// <para>
/// Once the model is final, the context indexes the event stream's sequence number where it is not the stream's key
/// — <c>ApplyGlobalEntityConfiguration</c> keys it by <c>Id</c> — and the entries not yet chained, so reading in
/// sequence order and chaining stay indexed whatever key the derived context leaves. A derived context that overrides
/// <see cref="ConfigureConventions"/> calls <c>base.ConfigureConventions</c>; a context that does not is refused when
/// its model is built, rather than running and migrating without those indexes.
/// </para>
/// </remarks>
/// <param name="options">Options bound by the host's <c>AddNpgsqlWriteDbContextFactory</c> registration.</param>
public class WriteDbContext<TContext>(DbContextOptions<TContext> options) : DbContext(options), IWriteDbContext where TContext : DbContext
{
    private bool _conventionsConfigured;

    /// <inheritdoc/>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Conventions.Add(services => new SequenceNumberIndexConvention(services.GetService<ISqlGenerationHelper>()));
        _conventionsConfigured = true;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// The derived context overrides <see cref="ConfigureConventions"/> without calling <c>base.ConfigureConventions</c>,
    /// so the model would lack the indexes the store's reads in sequence order and its hash chain depend on.
    /// </exception>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (!_conventionsConfigured)
        {
            throw new InvalidOperationException(
                $"The write store's model conventions did not run for {GetType().FullName ?? GetType().Name} before its model was built. "
                + "The write store declares the indexes on the event stream's sequence number there; "
                + "an override of ConfigureConventions must call base.ConfigureConventions(configurationBuilder).");
        }

        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(
            Assembly.GetAssembly(typeof(IWriteStoreMarker)) ?? throw new InvalidOperationException(),
            t => t.Namespace?.StartsWith("Stratara.EventSourcing.EntityFrameworkCore.WriteStore", StringComparison.Ordinal) == true);
        if (Database.IsNpgsql())
        {
            CommitOrderSchema.ApplyPostgresTransactionId(modelBuilder);
        }
    }
}
