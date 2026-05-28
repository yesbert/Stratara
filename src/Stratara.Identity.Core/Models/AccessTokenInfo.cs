using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.Core.Models;

/// <summary>
/// Holds the information about the currently issued access token: the authenticated user's email,
/// the raw <see cref="Models.LoginResponse"/> from the identity endpoint, and the absolute expiration timestamp
/// of the access token. Persisted in client-side secure storage to survive app restarts.
/// </summary>
/// <remarks>
/// <b>Sensitive data.</b> Because <see cref="LoginResponse"/> embeds the access token and the
/// refresh token (see the security remarks on <see cref="Models.LoginResponse"/>), the whole
/// <see cref="AccessTokenInfo"/> instance must be treated as a secret: persist only in encrypted /
/// platform-secure storage, never log it, never serialize it to observability sinks without
/// redacting the embedded tokens first.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class AccessTokenInfo
{
    /// <summary>Email address of the authenticated user.</summary>
    public required string Email { get; set; }

    /// <summary>The login response payload returned by the identity endpoint when the token was issued.</summary>
    /// <remarks>
    /// Contains the access and refresh tokens — see <see cref="Models.LoginResponse"/> for the
    /// secret-handling requirements that apply to this property.
    /// </remarks>
    public required LoginResponse LoginResponse { get; set; }

    /// <summary>Absolute UTC timestamp at which the access token expires.</summary>
    public required DateTimeOffset AccessTokenExpiration { get; set; }
}
