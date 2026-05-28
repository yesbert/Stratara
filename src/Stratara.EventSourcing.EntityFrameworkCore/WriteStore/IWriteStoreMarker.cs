namespace Stratara.EventSourcing.EntityFrameworkCore.WriteStore;

/// <summary>
/// Assembly marker for the write-store namespace. Used by <see cref="WriteDbContext{TContext}"/>
/// to scope <c>ApplyConfigurationsFromAssembly</c> to only the write-store
/// <c>IEntityTypeConfiguration&lt;&gt;</c> implementations — required because the same assembly
/// also hosts the read-store and identity-store configurations.
/// </summary>
public interface IWriteStoreMarker;
