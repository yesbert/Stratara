namespace Stratara.Abstractions.Authorization;

/// <summary>
/// Thrown by the authorizing mediator/dispatcher decorators when the caller does not hold a
/// permission required by <see cref="RequirePermissionAttribute"/> on the dispatched request
/// type. Derives from <see cref="AuthorizationException"/> so existing role-era handlers (403
/// mapping) catch permission denials without change.
/// </summary>
public sealed class PermissionAuthorizationException : AuthorizationException
{
    /// <summary>
    /// Initialise a new <see cref="PermissionAuthorizationException"/> for the named permission.
    /// </summary>
    /// <param name="permission">The permission the caller was missing.</param>
    public PermissionAuthorizationException(string permission)
        : base(permission, $"Access denied. Required permission: {permission}")
    {
        RequiredPermission = permission;
    }

    /// <summary>The permission the caller did not hold.</summary>
    public string RequiredPermission { get; }
}
