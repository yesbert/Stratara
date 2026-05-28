using Stratara.Abstractions.Entities;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.Projections.Multitenancy.Models;

/// <summary>
/// Read-model entity for the tenant aggregate. Maintained by <c>TenantProjection</c> from the
/// <c>Stratara.Domain</c> tenant lifecycle events.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class TenantView : IEntity
{
    /// <summary>Identifier of the customer that owns the tenant.</summary>
    public Guid CustomerId { get; set; }

    /// <summary>Display name of the tenant.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Default locale (IETF language tag) used by the tenant when no per-user override is set. Empty by default — the projected value comes from the <c>TenantCreated</c> event.</summary>
    public string DefaultLocale { get; set; } = string.Empty;

    /// <summary>Whether the tenant is active. Inactive tenants are blocked from sign-in but retained for history.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Timestamp at which the tenant was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Timestamp of the most recent modification, or <c>null</c> if the tenant has not been modified since creation.</summary>
    public DateTimeOffset? LastModifiedAt { get; set; }

    /// <summary>The aggregate version this view was last updated from. Used to make projection updates idempotent.</summary>
    public long SourceVersion { get; set; }

    /// <inheritdoc/>
    public Guid Id { get; set; }
}
