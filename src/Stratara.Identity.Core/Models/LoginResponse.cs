using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.Core.Models;

/// <summary>Response payload returned by the identity endpoint after a successful login or token refresh.</summary>
/// <remarks>
/// <para>
/// <b>Sensitive data.</b> This payload contains bearer credentials. Treat the entire object as a secret:
/// never log it, never include it in error messages, never serialize it to telemetry exporters without
/// redaction. The serialized JSON form is what gets persisted in <see cref="AccessTokenInfo"/>;
/// any place that handles that JSON must respect the same constraints.
/// </para>
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class LoginResponse
{
    /// <summary>The bearer-token type (typically <c>Bearer</c>).</summary>
    [JsonPropertyName("tokenType")] public required string TokenType { get; set; }

    /// <summary>
    /// The opaque access token to send in the <c>Authorization</c> header on subsequent requests.
    /// </summary>
    /// <remarks>
    /// <b>Treat as a secret.</b> A leaked access token grants full session privileges until
    /// <see cref="ExpiresIn"/> elapses. Never log it, never include it in exception messages,
    /// never expose it through tracing, metrics, or browser-visible state. Redact it before
    /// it reaches any observability sink.
    /// </remarks>
    [JsonPropertyName("accessToken")] public required string AccessToken { get; set; }

    /// <summary>Number of seconds from issue time after which the access token expires.</summary>
    [JsonPropertyName("expiresIn")] public required int ExpiresIn { get; set; }

    /// <summary>The refresh token used to obtain a new access token without re-authenticating.</summary>
    /// <remarks>
    /// <b>Treat as a secret — strictly more sensitive than <see cref="AccessToken"/>.</b>
    /// A leaked refresh token allows an attacker to mint fresh access tokens for the lifetime of
    /// the refresh-token policy (typically days to weeks). It must only be persisted in encrypted /
    /// platform-secure storage (e.g. the OS keychain or an HTTP-only cookie) and must never be
    /// logged, traced, returned to the browser, or sent to any non-identity endpoint.
    /// </remarks>
    [JsonPropertyName("refreshToken")] public required string RefreshToken { get; set; }
}
