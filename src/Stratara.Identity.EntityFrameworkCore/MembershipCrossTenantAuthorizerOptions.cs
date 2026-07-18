using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Options for <see cref="MembershipCrossTenantAuthorizer"/>.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class MembershipCrossTenantAuthorizerOptions
{
    /// <summary>
    /// Roles that permit an actor to operate on a tenant they hold no membership in — the
    /// operator-impersonation path (a platform administrator acting on a customer tenant).
    /// Each candidate role is checked through the registered
    /// <see cref="Stratara.Abstractions.Authorization.IAuthorizationProvider"/>, so it may live
    /// on either role level (tenant-scoped membership role or global Identity role). Empty by
    /// default: without configured roles, only actors with a membership in the target tenant
    /// pass.
    /// </summary>
    public ISet<string> CrossTenantRoles { get; } = new HashSet<string>(StringComparer.Ordinal);
}
