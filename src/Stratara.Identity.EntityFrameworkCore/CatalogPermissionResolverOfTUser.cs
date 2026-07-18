using Microsoft.AspNetCore.Identity;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Multitenancy;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Default <see cref="IPermissionResolver"/> spanning both role levels: the user's active
/// tenant-scoped membership roles <em>and</em> their global ASP.NET Identity roles (platform
/// roles) are mapped through the application's <see cref="PermissionCatalog"/> role grants —
/// so a grant like <c>GrantToRole("PlatformAdmin", ...)</c> works regardless of which level the
/// role lives on.
/// </summary>
/// <remarks>
/// Register scoped; resolved sets are memoized per <c>(userId, tenantId)</c> within the scope.
/// Fail-closed: unknown users and non-active memberships contribute no roles.
/// </remarks>
/// <typeparam name="TUser">The host's ASP.NET Identity user entity.</typeparam>
/// <param name="membershipStore">The membership store the tenant-scoped roles are read from.</param>
/// <param name="userManager">The Identity user manager the global roles are read from.</param>
/// <param name="catalog">The application's permission catalog (role grants).</param>
public sealed class CatalogPermissionResolver<TUser>(
    ITenantMembershipStore membershipStore,
    UserManager<TUser> userManager,
    PermissionCatalog catalog) : IPermissionResolver
    where TUser : class
{
    private readonly Dictionary<(Guid UserId, Guid TenantId), IReadOnlySet<string>> _cache = [];

    /// <inheritdoc/>
    public async ValueTask<IReadOnlySet<string>> ResolvePermissionsAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue((userId, tenantId), out var cached))
        {
            return cached;
        }

        var roles = new List<string>();

        var membership = await membershipStore.GetMembershipAsync(userId, tenantId, cancellationToken);
        if (membership is { Status: MembershipStatus.Active })
        {
            roles.AddRange(membership.Roles);
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is not null)
        {
            roles.AddRange(await userManager.GetRolesAsync(user));
        }

        var permissions = CatalogPermissionResolver.MapRolesThroughCatalog(catalog, roles);
        _cache[(userId, tenantId)] = permissions;
        return permissions;
    }
}
