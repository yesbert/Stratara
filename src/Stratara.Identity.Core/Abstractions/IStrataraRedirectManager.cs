namespace Stratara.Identity.Core.Abstractions;

/// <summary>
/// Channel-agnostic redirect surface used by post-authentication flows to send the user to a return URI.
/// Implemented per host (server-side Blazor uses <c>NavigationManager</c>; non-web hosts use their
/// platform-native navigation).
/// </summary>
public interface IStrataraRedirectManager
{
    /// <summary>Redirects to the given <paramref name="uri"/>, or to the host's default landing route if <c>null</c>.</summary>
    /// <param name="uri">Absolute or relative target URI; <c>null</c> for the host default.</param>
    void RedirectTo(string? uri);
}
