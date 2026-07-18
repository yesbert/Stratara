using Microsoft.AspNetCore.Authorization;
using Stratara.Abstractions.Authorization;
using Stratara.Identity.AspNetCore.Services;

namespace Stratara.Identity.AspNetCore.Authorization;

/// <summary>
/// Evaluates <see cref="PermissionRequirement"/> by resolving the caller's effective permission
/// set through the registered <see cref="IPermissionResolver"/> — the user id comes from the
/// principal's name-identifier claim, the tenant scope from the <c>stratara:tenant_id</c> claim
/// (the one the membership sign-in bridge stamps).
/// </summary>
/// <remarks>
/// Fail-closed: principals without a parseable user id or tenant claim never satisfy the
/// requirement. The resolver is looked up per evaluation (the handler is registered scoped), so
/// DB-backed resolution with per-scope memoization applies here exactly as in the mediator path
/// — permissions are never read from claims.
/// </remarks>
/// <param name="permissionResolver">The resolver supplying the caller's effective permissions.</param>
public sealed class PermissionAuthorizationHandler(
    IPermissionResolver permissionResolver) : AuthorizationHandler<PermissionRequirement>
{
    /// <inheritdoc/>
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (MembershipTenantClaimResolver.GetUserId(context.User) is not { } userId
            || MembershipTenantClaimResolver.GetTenantId(context.User) is not { } tenantId)
        {
            return;
        }

        var held = await permissionResolver.ResolvePermissionsAsync(userId, tenantId);
        if (held.Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}
