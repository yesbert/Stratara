using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;

namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore;

/// <summary>
/// Base read-store DbContext that consumer DbContexts derive from. Hosts the projection /
/// read-model tables that the projection workers write into and queries read from.
/// </summary>
/// <typeparam name="TContext">The concrete derived DbContext type (used for <see cref="DbContextOptions{TContext}"/> binding).</typeparam>
/// <remarks>
/// Filters <c>ApplyConfigurationsFromAssembly</c> by the <c>Stratara.EventSourcing.EntityFrameworkCore.ReadStore</c> namespace so
/// the write-store and identity-store <c>IEntityTypeConfiguration&lt;&gt;</c> implementations
/// co-hosted in the same assembly do not leak into the read model. Missing the namespace
/// predicate yields <c>PendingModelChangesWarning</c> at runtime — a regression that escapes
/// unit tests and only surfaces against a real Postgres in the consumer's E2E pipeline.
/// </remarks>
/// <param name="options">Options bound by the host's <c>AddNpgsqlReadDbContextFactory</c> registration.</param>
public class ReadDbContext<TContext>(DbContextOptions<TContext> options) : DbContext(options), IReadDbContext where TContext : DbContext
{
    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(
            Assembly.GetAssembly(typeof(IReadStoreMarker)) ?? throw new InvalidOperationException(),
            t => t.Namespace?.StartsWith("Stratara.EventSourcing.EntityFrameworkCore.ReadStore", StringComparison.Ordinal) == true);
    }
}
