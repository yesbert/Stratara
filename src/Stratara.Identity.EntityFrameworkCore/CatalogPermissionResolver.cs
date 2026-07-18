using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Multitenancy;

namespace Stratara.Identity.EntityFrameworkCore;

/// <summary>
/// Default <see cref="IPermissionResolver"/>: maps the user's active tenant-scoped membership
/// roles through the application's <see cref="PermissionCatalog"/> role grants. Use the generic
/// variant (<see cref="CatalogPermissionResolver{TUser}"/>) when global ASP.NET Identity roles
/// (platform roles) should also grant permissions.
/// </summary>
/// <remarks>
/// Register scoped; resolved sets are memoized per <c>(userId, tenantId)</c> within the scope,
/// so repeated checks in one request incur one membership lookup. Fail-closed: no membership or
/// a non-active membership yields an empty set.
/// </remarks>
/// <param name="membershipStore">The membership store the roles are read from.</param>
/// <param name="catalog">The application's permission catalog (role grants).</param>
public sealed class CatalogPermissionResolver(
    ITenantMembershipStore membershipStore,
    PermissionCatalog catalog) : IPermissionResolver
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

        var roles = await CollectRolesAsync(userId, tenantId, cancellationToken);
        var permissions = MapRolesThroughCatalog(catalog, roles);
        _cache[(userId, tenantId)] = permissions;
        return permissions;
    }

    private async Task<IReadOnlyCollection<string>> CollectRolesAsync(
        Guid userId, Guid tenantId, CancellationToken cancellationToken)
    {
        var membership = await membershipStore.GetMembershipAsync(userId, tenantId, cancellationToken);
        return membership is { Status: MembershipStatus.Active } ? membership.Roles : [];
    }

    internal static IReadOnlySet<string> MapRolesThroughCatalog(
        PermissionCatalog catalog, IEnumerable<string> roles)
    {
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            permissions.UnionWith(catalog.GetRolePermissions(role));
        }

        return permissions;
    }
}
