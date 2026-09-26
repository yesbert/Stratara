namespace Stratara.Projections.Abstractions;

/// <summary>
/// A projection that forgets a tenant once the tenant is deleted. The framework hands it the
/// tenant-deletion facts it ships — <c>TenantDeleted</c> and <c>CustomerTenantsDeleted</c> — whether or
/// not the projection handles them, and after each one records, for this projection alone, the tenants
/// it deleted. When the projection later throws
/// <see cref="Stratara.Abstractions.EventSourcing.PrecedingFactMissingException"/> for a fact owned by
/// such a tenant, the fact is passed over and logged instead of being retried and failing.
/// </summary>
/// <remarks>
/// <para>
/// Declare it on a projection that removes a deleted tenant's rows. A fact recorded for that tenant
/// after the deletion — work that was queued before it and ran to its end — then finds nothing to apply
/// to, and would otherwise dead-letter live and fail every replay. Removing the tenant's rows stays the
/// projection's own job; the framework only remembers that it happened.
/// </para>
/// <para>
/// Only the outcome of a missing-prerequisite report changes. A fact of a deleted tenant that the
/// projection applies without complaint is applied, and a report for any other tenant is retried and
/// fails as before. The record is kept by the registered <see cref="IForgottenTenantStore"/>; a
/// declaring projection in a host without one fails on the first fact it is handed. A replay empties
/// the record with the read models, and a projection rebuilt on its own has its record emptied with its
/// read model.
/// </para>
/// </remarks>
public interface IForgetsDeletedTenants : IProjection;
