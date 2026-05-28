using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Authorization;

/// <summary>
/// Marks a command or query type as requiring the caller to be in a specific role. The
/// authorizing mediator decorator (<c>AddAuthorizingMediator&lt;T&gt;()</c>) checks the
/// attribute on dispatch and throws <see cref="AuthorizationException"/> if the
/// <see cref="IAuthorizationProvider"/> reports the role is missing.
/// </summary>
/// <remarks>
/// Multiple attributes on the same target are ANDed — every listed role must be present.
/// Apply only to request types (commands/queries), not handlers — the check runs before the
/// handler is resolved. The <c>AuthorizationStartupValidator</c> hosted service fails fast at
/// startup if any <see cref="RequireRoleAttribute"/>-decorated type is loaded but no
/// <see cref="IAuthorizationProvider"/> is registered, so misconfigured hosts crash rather than
/// silently dispatching unprotected.
/// </remarks>
/// <param name="role">The role name the caller must hold.</param>
/// <example>
/// Annotate a sensitive admin command:
/// <code>
/// [RequireRole("PlatformAdmin")]
/// public sealed record SuspendTenant(Guid TenantId) : ICommand;
/// </code>
/// Register the authorizing decorator at host composition:
/// <code>
/// services.AddAuthorizingMediator&lt;MyAuthorizationProvider&gt;();
/// </code>
/// </example>
[ExcludeFromCodeCoverage]
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class RequireRoleAttribute(string role) : Attribute
{
    /// <summary>The role name the caller must hold.</summary>
    public string Role { get; } = role;
}
