using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

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
/// </remarks>
/// <param name="options">Options bound by the host's <c>AddNpsqlWriteDbContextFactory</c> registration.</param>
public class WriteDbContext<TContext>(DbContextOptions<TContext> options) : DbContext(options), IWriteDbContext where TContext : DbContext
{
    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(
            Assembly.GetAssembly(typeof(IWriteStoreMarker)) ?? throw new InvalidOperationException(),
            t => t.Namespace?.StartsWith("Stratara.EventSourcing.EntityFrameworkCore.WriteStore", StringComparison.Ordinal) == true);
    }
}
