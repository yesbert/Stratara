namespace Stratara.EventSourcing.EntityFrameworkCore.IdentityStore;

/// <summary>
/// Assembly marker for the identity-store namespace. Used by <see cref="IdentityStore{TContext,TUser}"/>
/// to scope <c>ApplyConfigurationsFromAssembly</c> to only the identity-store
/// <c>IEntityTypeConfiguration&lt;&gt;</c> implementations — required after the W2.2 mega-fold
/// because the same assembly also hosts the write- and read-store configurations.
/// </summary>
public interface IIdentityStoreMarker;
