namespace Stratara.EventSourcing.EntityFrameworkCore.ReadStore;

/// <summary>
/// Assembly marker for the read-store namespace. Used by <see cref="ReadDbContext{TContext}"/>
/// to scope <c>ApplyConfigurationsFromAssembly</c> to only the read-store
/// <c>IEntityTypeConfiguration&lt;&gt;</c> implementations — required because the same assembly
/// also hosts the write-store and identity-store configurations.
/// </summary>
public interface IReadStoreMarker;
