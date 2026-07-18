namespace Stratara.Abstractions.Authorization;

/// <summary>
/// Declares that dispatching the decorated command/query requires the caller to hold the named
/// permission — the fine-grained sibling of <see cref="RequireRoleAttribute"/>. Enforced by the
/// authorizing mediator and the authorizing outbox dispatcher before the handler runs; multiple
/// attributes on the same request are ANDed (every permission must be held).
/// </summary>
/// <remarks>
/// <para>
/// Permission names are application vocabulary (for example <c>"sims.read"</c>), declared once
/// in the application's <see cref="PermissionCatalog"/> and resolved for the current actor via
/// the registered <see cref="IPermissionResolver"/>. Roles and permissions layer: coarse
/// role gates (<see cref="RequireRoleAttribute"/>) and fine-grained permission gates may be
/// combined freely on the same request type.
/// </para>
/// <para>
/// Fail-fast wiring: the mediator's startup validator rejects hosts that carry
/// <c>[RequirePermission]</c> types without an authorizing mediator and a registered
/// <see cref="IPermissionResolver"/>, so the attribute can never be silently ignored.
/// </para>
/// </remarks>
/// <param name="permission">The permission the caller must hold.</param>
/// <example>
/// <code>
/// [RequirePermission("sims.read")]
/// public sealed record GetSimCardsQuery(Guid TenantId) : IQuery&lt;IReadOnlyList&lt;SimCardDto&gt;&gt;;
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string permission) : Attribute
{
    /// <summary>The permission the caller must hold to dispatch the decorated request.</summary>
    public string Permission { get; } = permission;
}
