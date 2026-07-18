using System.Diagnostics.CodeAnalysis;

namespace Stratara.Identity.AspNetCore.Authentication;

/// <summary>
/// Bindable configuration for the Stratara JWT-bearer helper — validating access tokens on API
/// requests (the machine/service-to-service and SPA/mobile counterpart to the interactive OpenID
/// Connect flow). Bound from a configuration section (default <c>Identity:JwtBearer</c>) and applied
/// to the ASP.NET Core <c>JwtBearer</c> handler by
/// <see cref="M:Microsoft.Extensions.DependencyInjection.OpenIdConnectAuthenticationExtensions.AddStrataraJwtBearer(Microsoft.AspNetCore.Authentication.AuthenticationBuilder,Microsoft.Extensions.Configuration.IConfiguration,System.String,System.String)"/>.
/// </summary>
/// <remarks>
/// A single API can accept tokens from several issuers (multi-tenant Entra, Keycloak, an internal
/// authority) by listing them in <see cref="ValidIssuers"/> — the handler routes by the token's
/// <c>iss</c>. The principal's name-identifier is kept as the issuer <c>sub</c> so a bearer request
/// flows through the same membership/permission plane as an interactive sign-in.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class StrataraJwtBearerOptions
{
    /// <summary>The default configuration section this options object binds from.</summary>
    public const string DefaultSectionName = "Identity:JwtBearer";

    /// <summary>
    /// The primary token authority (issuer) URL used to discover signing keys. Optional when
    /// <see cref="ValidIssuers"/> and explicit metadata are supplied.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>The audience (<c>aud</c>) the token must be issued for — this API's identifier.</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Additional acceptable issuers for multi-issuer APIs. When set, the token's <c>iss</c> must
    /// match one of these (or <see cref="Authority"/>); leave empty to trust only
    /// <see cref="Authority"/>.
    /// </summary>
    public IList<string> ValidIssuers { get; set; } = [];

    /// <summary>
    /// Whether HTTPS is required for the authority metadata endpoint. Defaults to <c>true</c>;
    /// set to <c>false</c> only for local development.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// The claim type read as the principal's name-identifier. Defaults to <c>sub</c> so the
    /// bearer principal is keyed on the stable subject identifier, never a mutable email.
    /// </summary>
    public string NameClaimType { get; set; } = "sub";

    /// <summary>The claim type read for roles. Defaults to <c>roles</c> (the Entra/OIDC convention).</summary>
    public string RoleClaimType { get; set; } = "roles";
}
