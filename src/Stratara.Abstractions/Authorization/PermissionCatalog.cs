namespace Stratara.Abstractions.Authorization;

/// <summary>
/// The application's permission vocabulary plus its role→permission grants, declared in code at
/// startup and registered as a singleton. The catalog is the single source of what permission
/// names exist; role grants map coarse roles (tenant-scoped membership roles or global platform
/// roles alike) onto sets of fine-grained permissions.
/// </summary>
/// <remarks>
/// <para>
/// Declaration is strict by design: granting an undeclared permission throws, so a typo in a
/// grant surfaces at startup instead of silently never matching. Reads are lock-free; build the
/// catalog completely during service registration and treat it as immutable afterwards.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// services.AddPermissionCatalog(catalog =>
/// {
///     catalog.Add("sims.read", "sims.write", "billing.read");
///     catalog.GrantToRole("TenantAdmin", "sims.read", "sims.write", "billing.read");
///     catalog.GrantToRole("Support", "sims.read");
/// });
/// </code>
/// </example>
public sealed class PermissionCatalog
{
    private readonly HashSet<string> _permissions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _roleGrants = new(StringComparer.Ordinal);

    /// <summary>Every permission name declared in the catalog.</summary>
    public IReadOnlyCollection<string> All => _permissions;

    /// <summary>
    /// Declares one or more permission names. Redeclaring an existing name is a no-op.
    /// </summary>
    /// <param name="permissions">The permission names to declare.</param>
    /// <exception cref="ArgumentException">A name is null, empty, or whitespace.</exception>
    public void Add(params string[] permissions)
    {
        foreach (var permission in permissions)
        {
            if (string.IsNullOrWhiteSpace(permission))
            {
                throw new ArgumentException("Permission names must be non-empty.", nameof(permissions));
            }

            _permissions.Add(permission);
        }
    }

    /// <summary>
    /// Grants declared permissions to a role; cumulative across calls for the same role.
    /// </summary>
    /// <param name="role">The role receiving the grants.</param>
    /// <param name="permissions">Previously declared permission names.</param>
    /// <exception cref="ArgumentException">
    /// The role name is empty, or a permission was not declared via <see cref="Add"/> first
    /// (strictness catches grant typos at startup).
    /// </exception>
    public void GrantToRole(string role, params string[] permissions)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new ArgumentException("Role names must be non-empty.", nameof(role));
        }

        foreach (var permission in permissions.Where(permission => !_permissions.Contains(permission)))
        {
            throw new ArgumentException(
                $"Permission '{permission}' is not declared in the catalog; call Add(\"{permission}\") before granting it.",
                nameof(permissions));
        }

        if (!_roleGrants.TryGetValue(role, out var grants))
        {
            grants = new HashSet<string>(StringComparer.Ordinal);
            _roleGrants[role] = grants;
        }

        grants.UnionWith(permissions);
    }

    /// <summary>
    /// Checks whether the permission name is declared in the catalog.
    /// </summary>
    /// <param name="permission">The permission name to check.</param>
    /// <returns><c>true</c> when declared.</returns>
    public bool Contains(string permission) => _permissions.Contains(permission);

    /// <summary>
    /// Gets the permissions granted to the role; empty for roles without grants.
    /// </summary>
    /// <param name="role">The role whose grants to read.</param>
    /// <returns>The granted permission set.</returns>
    public IReadOnlySet<string> GetRolePermissions(string role) =>
        _roleGrants.TryGetValue(role, out var grants) ? grants : new HashSet<string>(StringComparer.Ordinal);
}
