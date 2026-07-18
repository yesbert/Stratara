namespace Stratara.Abstractions.Authorization;

/// <summary>
/// Thrown by the authorizing mediator/dispatcher decorators when the caller does not hold a
/// role required by <see cref="RequireRoleAttribute"/> on the dispatched request type.
/// Permission failures (<see cref="RequirePermissionAttribute"/>) throw the derived
/// <see cref="PermissionAuthorizationException"/>, so a single catch of this base type maps
/// every authorization denial (typically to HTTP 403).
/// </summary>
public class AuthorizationException : Exception
{
    /// <summary>
    /// Initialise a new <see cref="AuthorizationException"/> for the named role.
    /// </summary>
    /// <param name="role">The role the caller was missing.</param>
    public AuthorizationException(string role)
        : base($"Access denied. Required role: {role}")
    {
        RequiredRole = role;
    }

    /// <summary>
    /// Initialise with a custom message; used by derived denial types.
    /// </summary>
    /// <param name="requirement">The requirement the caller was missing (mirrored into <see cref="RequiredRole"/>).</param>
    /// <param name="message">The exception message.</param>
    protected AuthorizationException(string requirement, string message)
        : base(message)
    {
        RequiredRole = requirement;
    }

    /// <summary>
    /// The requirement the caller did not hold. For role denials this is the role name; derived
    /// denial types mirror their specific requirement here so role-era handlers keep logging
    /// something meaningful.
    /// </summary>
    public string RequiredRole { get; }
}
