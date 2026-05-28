using System.Diagnostics.CodeAnalysis;

namespace Stratara.Domain;

/// <summary>Tenant-aggregate creation event — opens a new tenant stream.</summary>
/// <param name="Id">Tenant identifier (also the event-stream id).</param>
/// <param name="CustomerId">Customer that owns the new tenant.</param>
/// <param name="Name">Human-readable tenant name.</param>
/// <param name="DefaultLocale">IETF BCP 47 default locale (e.g. <c>de-DE</c>).</param>
/// <param name="IsActive">Whether the tenant is active immediately on creation.</param>
/// <param name="CreatedAt">Creation timestamp.</param>
[ExcludeFromCodeCoverage]
public sealed record TenantCreated(Guid Id, Guid CustomerId, string Name, string DefaultLocale, bool IsActive, DateTimeOffset CreatedAt);

/// <summary>Soft-delete event for a tenant — sets the aggregate's <c>DeletedAt</c> stamp.</summary>
/// <param name="DeletedAt">When the tenant was soft-deleted.</param>
[ExcludeFromCodeCoverage]
public sealed record TenantDeleted(DateTimeOffset DeletedAt);

/// <summary>
/// Cascade event indicating that all tenants of a customer were deleted in one operation
/// (typically when the customer aggregate itself is being removed).
/// </summary>
/// <param name="CustomerId">The customer whose tenants are being removed.</param>
/// <param name="TenantIds">The list of affected tenant ids.</param>
/// <param name="DeletedAt">Timestamp of the cascade operation.</param>
[ExcludeFromCodeCoverage]
public sealed record CustomerTenantsDeleted(Guid CustomerId, IReadOnlyList<Guid> TenantIds, DateTimeOffset DeletedAt);

/// <summary>Rename event — updates the tenant's <c>Name</c>.</summary>
/// <param name="Name">The new tenant name.</param>
/// <param name="ChangedAt">Timestamp of the rename.</param>
[ExcludeFromCodeCoverage]
public sealed record TenantRenamed(string Name, DateTimeOffset ChangedAt);

/// <summary>Activation event — flips <c>IsActive</c> back to <c>true</c>.</summary>
/// <param name="ChangedAt">Activation timestamp.</param>
[ExcludeFromCodeCoverage]
public sealed record TenantActivated(DateTimeOffset ChangedAt);

/// <summary>Deactivation event — flips <c>IsActive</c> to <c>false</c>.</summary>
/// <param name="ChangedAt">Deactivation timestamp.</param>
[ExcludeFromCodeCoverage]
public sealed record TenantDeactivated(DateTimeOffset ChangedAt);

/// <summary>Default-locale change event — swaps the tenant's BCP 47 default locale.</summary>
/// <param name="DefaultLocale">New IETF BCP 47 locale tag.</param>
/// <param name="ChangedAt">Timestamp of the change.</param>
[ExcludeFromCodeCoverage]
public sealed record TenantDefaultLocaleChanged(string DefaultLocale, DateTimeOffset ChangedAt);

/// <summary>Customer-reassignment event — re-parents the tenant to a different customer.</summary>
/// <param name="CustomerId">New owner customer id.</param>
/// <param name="ChangedAt">Reassignment timestamp.</param>
[ExcludeFromCodeCoverage]
public sealed record TenantAssignedToCustomer(Guid CustomerId, DateTimeOffset ChangedAt);
