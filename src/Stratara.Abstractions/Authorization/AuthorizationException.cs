namespace Stratara.Abstractions.Authorization;

/// <summary>
/// Thrown by the authorizing mediator/dispatcher decorators when the caller does not hold a
/// role required by <see cref="RequireRoleAttribute"/> on the dispatched request type.
/// </summary>
public sealed class AuthorizationException : Exception
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

    /// <summary>The role name the caller did not hold.</summary>
    public string RequiredRole { get; }
}
