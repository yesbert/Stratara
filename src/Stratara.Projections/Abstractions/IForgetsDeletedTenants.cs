namespace Stratara.Projections.Abstractions;

/// <summary>
/// A projection that forgets a tenant once the tenant is deleted. The framework hands it the
/// tenant-deletion facts it ships — <c>TenantDeleted</c> and <c>CustomerTenantsDeleted</c> — whether or
/// not the projection handles them, and after each one records, for this projection alone, the tenants
/// it deleted. A later fact owned by such a tenant is passed over and logged, without calling the
/// projection, whether it would have added a row or reported
/// <see cref="Stratara.Abstractions.EventSourcing.PrecedingFactMissingException"/>.
/// </summary>
/// <remarks>
/// <para>
/// Declaring it is a promise: once either deletion fact has been applied, the tenant's data is gone from
/// this projection's read model — removed by the projection's own handlers or by anything else. A fact
/// recorded for that tenant after the deletion — work that was queued before it and ran to its end — has
/// nothing to apply to, and readers partitioned by stream can also meet an older fact of the tenant after
/// the deletion. Removing the data stays the projection's own job; the framework only remembers that it
/// happened. A projection that keeps a tenant's rows after <c>TenantDeleted</c>, the tenant's soft delete,
/// must not declare it: its facts for that tenant would be passed over for good.
/// </para>
/// <para>
/// The decision is made on the fact's owning tenant, before handling, and costs one read of the registered
/// <see cref="IForgottenTenantStore"/> per fact, other than a deletion fact or a fact with an empty tenant.
/// The deletion facts are always handled. A fact of a recorded tenant whose handler would remove something
/// elsewhere is passed over too, so what it would have removed stays; a projection that needs such a fact
/// applied must not declare this interface. The read and the handler are not one transaction, so a deletion
/// applied by another partition between them can still leave the row of a fact that was already past the read.
/// </para>
/// <para>
/// A missing-prerequisite report for any other tenant is retried and fails as before, and so does a fact
/// the store could not be asked about. A declaring projection in a host without a store fails on the first
/// fact it is handed. A replay empties the record of each declaring projection it rebuilds just before the
/// read models, and a projection rebuilt on its own on the Orleans execution model has its record emptied
/// just before its read model.
/// </para>
/// </remarks>
public interface IForgetsDeletedTenants : IProjection;
