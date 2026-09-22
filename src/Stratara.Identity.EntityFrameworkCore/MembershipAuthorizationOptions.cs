using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Options for the membership-backed <see cref="Stratara.Abstractions.Authorization.IAuthorizationProvider"/>
/// implementations.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class MembershipAuthorizationOptions
{
    /// <summary>
    /// Roles that are resolved in the tenant the actor is a member of rather than in the tenant the
    /// request concerns — for an actor acting on a tenant it holds no membership in: a machine key,
    /// which materialises one membership in the tenant it was issued for and serves many, or an
    /// operator administering a tenant they never joined.
    /// <para>
    /// Consulted only when the session's actor tenant differs from its data-owner tenant, and only
    /// after the actor's membership in the data-owner tenant yielded no answer. Empty by default:
    /// without a named role, every check resolves in the data-owner tenant as it always has. Only the
    /// roles named here cross — an actor's other roles stay in its own tenant.
    /// </para>
    /// </summary>
    public ISet<string> HomeTenantRoles { get; } = new HashSet<string>(StringComparer.Ordinal);
}
