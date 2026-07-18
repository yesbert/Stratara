using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// EF Core row shape for the <c>tenant_membership</c> table — one user's membership in one
/// tenant with the tenant-scoped roles the user holds there. Persistence twin of the
/// <see cref="Stratara.Abstractions.Multitenancy.TenantMembership"/> contract record.
/// </summary>
[ExcludeFromCodeCoverage]
public class TenantMembershipEntry
{
    /// <summary>The user who holds the membership (composite-key part 1).</summary>
    public Guid UserId { get; set; }

    /// <summary>The tenant the membership belongs to (composite-key part 2).</summary>
    public Guid TenantId { get; set; }

    /// <summary>Tenant-scoped role names the user holds within the tenant (JSON column).</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>Lifecycle state of the membership, stored as its enum name.</summary>
    public Stratara.Abstractions.Multitenancy.MembershipStatus Status { get; set; }
}
