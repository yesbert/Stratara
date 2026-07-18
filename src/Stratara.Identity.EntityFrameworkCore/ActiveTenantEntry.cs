using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// EF Core row shape for the <c>active_tenant</c> table — a user's explicit selection of which
/// of their tenant memberships is the active one at sign-in (the tenant-switch persistence).
/// At most one row per user.
/// </summary>
[ExcludeFromCodeCoverage]
public class ActiveTenantEntry
{
    /// <summary>The user the selection belongs to (primary key).</summary>
    public Guid UserId { get; set; }

    /// <summary>The tenant the user selected as active.</summary>
    public Guid TenantId { get; set; }
}
