using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Multitenancy;

/// <summary>
/// One user's membership in one tenant, carrying the tenant-scoped roles the user holds there.
/// The user↔tenant relationship is many-to-many: a user may hold any number of memberships,
/// each with its own role set, and single-tenant applications are simply the degenerate case
/// of one membership per user.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Roles"/> are <em>tenant-scoped</em> role names (for example a tenant-administrator
/// role) that apply only within <see cref="TenantId"/>. They are independent of any global
/// platform roles the identity system may manage elsewhere (such as ASP.NET Identity's role
/// store); authorization components that consume memberships are expected to consult both levels.
/// </para>
/// <para>
/// A membership with <see cref="MembershipStatus.Pending"/> status represents an invitation that
/// has not been accepted yet and confers no access.
/// </para>
/// </remarks>
/// <param name="UserId">The user who holds the membership.</param>
/// <param name="TenantId">The tenant the membership belongs to.</param>
/// <param name="Roles">
/// Tenant-scoped role names the user holds within <paramref name="TenantId"/>. Never
/// <c>null</c>; empty when the user is a plain member without elevated tenant roles.
/// </param>
/// <param name="Status">Lifecycle state of the membership; defaults to <see cref="MembershipStatus.Active"/>.</param>
[ExcludeFromCodeCoverage]
public sealed record TenantMembership(
    Guid UserId,
    Guid TenantId,
    IReadOnlyCollection<string> Roles,
    MembershipStatus Status = MembershipStatus.Active);
