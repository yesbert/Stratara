using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.AspNetCore.Authentication;

/// <summary>
/// Bindable configuration for the Stratara OpenID Connect helper — the interactive
/// "log in with &lt;provider&gt;" flow (Microsoft Entra, Keycloak, generic OIDC/Google).
/// Bound from a configuration section (default <c>Identity:OpenIdConnect</c>) and applied to the
/// ASP.NET Core <c>OpenIdConnect</c> handler by
/// <see cref="M:Microsoft.Extensions.DependencyInjection.OpenIdConnectAuthenticationExtensions.AddStrataraOpenIdConnect(Microsoft.AspNetCore.Authentication.AuthenticationBuilder,Microsoft.Extensions.Configuration.IConfiguration,System.String,System.String)"/>.
/// </summary>
/// <remarks>
/// Entra vs. Keycloak vs. generic OIDC differ only in these three config blocks — authority,
/// client credentials, and scopes; the handler wiring is identical. External logins are linked by
/// the issuer's <c>sub</c> (never by email — see the JIT provisioning security invariants), so the
/// helper keeps <c>sub</c> as the principal's name-identifier.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class StrataraOpenIdConnectOptions
{
    /// <summary>The default configuration section this options object binds from.</summary>
    public const string DefaultSectionName = "Identity:OpenIdConnect";

    /// <summary>
    /// The scopes applied when <see cref="Scopes"/> is left empty — <c>openid</c>, <c>profile</c>,
    /// <c>email</c>. <c>email</c> is required so the JIT provisioning verified-email checks can run.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultScopes = ["openid", "profile", "email"];

    /// <summary>
    /// The OIDC authority (issuer) URL — for example
    /// <c>https://login.microsoftonline.com/&lt;tenant&gt;/v2.0</c> (Entra) or
    /// <c>https://keycloak.example.com/realms/&lt;realm&gt;</c> (Keycloak).
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>The application (client) id registered with the provider.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// The client secret for the confidential-client authorization-code flow. Leave unset for
    /// public clients using PKCE only.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// The path the provider redirects back to after authentication. Defaults to
    /// <c>/signin-oidc</c> (the ASP.NET Core convention).
    /// </summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    /// <summary>
    /// The path the provider redirects back to after sign-out. Defaults to
    /// <c>/signout-callback-oidc</c>.
    /// </summary>
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";

    /// <summary>
    /// The OAuth/OIDC response type. Defaults to <c>code</c> (authorization-code flow with PKCE),
    /// the only flow recommended for server-side apps.
    /// </summary>
    public string ResponseType { get; set; } = "code";

    /// <summary>
    /// The scopes requested from the provider. Left empty by default; when empty the helper applies
    /// <see cref="DefaultScopes"/>. Set this to request a specific set (it replaces the defaults).
    /// </summary>
    public IList<string> Scopes { get; set; } = [];

    /// <summary>
    /// Whether HTTPS is required for the authority metadata endpoint. Defaults to <c>true</c>;
    /// set to <c>false</c> only for local development against a plain-HTTP provider.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Whether the handler stores the issued tokens in the authentication properties. Defaults to
    /// <c>true</c> so downstream code (for example the JIT provisioning callback) can read the
    /// external principal's claims.
    /// </summary>
    public bool SaveTokens { get; set; } = true;

    /// <summary>
    /// Whether to call the provider's user-info endpoint to enrich the principal. Defaults to
    /// <c>true</c> — some providers assert <c>email</c>/<c>email_verified</c> only there.
    /// </summary>
    public bool GetClaimsFromUserInfoEndpoint { get; set; } = true;
}
