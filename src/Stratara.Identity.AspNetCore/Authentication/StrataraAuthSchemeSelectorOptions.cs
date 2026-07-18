using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Identity;

namespace Stratara.Identity.AspNetCore.Authentication;

/// <summary>
/// Options for the Stratara auth-scheme selector — the policy scheme that routes each request
/// to the matching authentication scheme by shape: API-key header present → API-key scheme;
/// <c>Authorization: Bearer</c> → bearer scheme; everything else → the fallback (cookie) scheme.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class StrataraAuthSchemeSelectorOptions
{
    /// <summary>The selector's own scheme name — <c>StrataraAuthSelector</c>.</summary>
    public const string SchemeName = "StrataraAuthSelector";

    /// <summary>The header whose presence routes to the API-key scheme. Defaults to <c>X-Api-Key</c>.</summary>
    public string ApiKeyHeaderName { get; set; } = ApiKeyAuthenticationOptions.DefaultHeaderName;

    /// <summary>The scheme handling API-key requests. Defaults to <see cref="ApiKeyAuthenticationOptions.SchemeName"/>.</summary>
    public string ApiKeyScheme { get; set; } = ApiKeyAuthenticationOptions.SchemeName;

    /// <summary>The scheme handling <c>Authorization: Bearer</c> requests. Defaults to ASP.NET Identity's bearer scheme.</summary>
    public string BearerScheme { get; set; } = IdentityConstants.BearerScheme;

    /// <summary>The scheme handling every other request. Defaults to ASP.NET Identity's application cookie scheme.</summary>
    public string FallbackScheme { get; set; } = IdentityConstants.ApplicationScheme;
}
