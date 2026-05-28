using Stratara.Identity.Core.Models;

namespace Stratara.Identity.Core.Abstractions;

/// <summary>
/// Channel-agnostic secure-storage surface for persisting and reading the current
/// <see cref="AccessTokenInfo"/>. Implemented per host (server-side Blazor typically relies on the
/// ASP.NET Core auth cookie and provides a no-op; non-web hosts use platform-native secure storage).
/// </summary>
/// <remarks>
/// Implementations handle bearer credentials (see the security remarks on
/// <see cref="Stratara.Identity.Core.Models.LoginResponse"/>) and MUST persist them only in
/// encrypted / platform-secure storage — e.g. the OS keychain on desktop / mobile, an HTTP-only
/// cookie under TLS on the server. Never write tokens to plain files, unsecured preferences,
/// or unencrypted application state.
/// </remarks>
public interface ITokenStorage
{
    /// <summary>Removes any stored token from secure storage.</summary>
    void RemoveToken();

    /// <summary>Reads the persisted <see cref="AccessTokenInfo"/> from secure storage.</summary>
    /// <param name="cancellationToken">Propagated to the underlying secure-storage read.</param>
    /// <returns>The stored token info, or <c>null</c> when nothing is persisted or deserialization fails.</returns>
    Task<AccessTokenInfo?> GetTokenInfoFromSecureStorageAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists a freshly issued token to secure storage.</summary>
    /// <param name="token">Raw token JSON as returned by the identity endpoint.</param>
    /// <param name="email">Email address of the authenticated user.</param>
    /// <param name="cancellationToken">Propagated to the underlying secure-storage write.</param>
    /// <returns>The constructed <see cref="AccessTokenInfo"/> after writing, or <c>null</c> on failure.</returns>
    Task<AccessTokenInfo?> SaveTokenToSecureStorageAsync(string token, string email, CancellationToken cancellationToken = default);
}
