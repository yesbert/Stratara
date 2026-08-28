using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Stratara.Identity.AspNetCore.Authentication;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Authentication-builder extensions for external identity providers — the interactive OpenID
/// Connect sign-in flow and API JWT-bearer validation. Both are ordinary ASP.NET Core schemes (the
/// provider seam <em>is</em> the scheme), so they compose with the cookie, API-key, and membership
/// wiring already in this package. Entra, Keycloak, and generic OIDC differ only in configuration.
/// </summary>
public static class OpenIdConnectAuthenticationExtensions
{
    /// <summary>
    /// Register the interactive OpenID Connect sign-in scheme from configuration (default section
    /// <c>Identity:OpenIdConnect</c>). Pair it with a cookie scheme for the signed-in session and,
    /// for first-time external users, <c>AddStrataraExternalLoginProvisioning</c> to create/link the
    /// local account. The external login is keyed on the issuer <c>sub</c>, never on email.
    /// </summary>
    /// <param name="builder">The authentication builder.</param>
    /// <param name="configuration">The configuration root or section parent to bind from.</param>
    /// <param name="sectionName">The configuration section to bind. Defaults to <c>Identity:OpenIdConnect</c>.</param>
    /// <param name="scheme">The scheme name to register. Defaults to the OIDC default scheme.</param>
    /// <returns>The same builder, to enable chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddAuthentication(StrataraAuthSchemeSelectorOptions.SchemeName)
    ///     .AddStrataraOpenIdConnect(builder.Configuration)
    ///     .AddStrataraAuthSchemeSelector();
    /// </code>
    /// </example>
    public static AuthenticationBuilder AddStrataraOpenIdConnect(
        this AuthenticationBuilder builder,
        IConfiguration configuration,
        string sectionName = StrataraOpenIdConnectOptions.DefaultSectionName,
        string scheme = OpenIdConnectDefaults.AuthenticationScheme)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = BindOpenIdConnect(configuration, sectionName);
        return builder.AddOpenIdConnect(scheme, oidc => ApplyTo(options, oidc));
    }

    /// <summary>
    /// Register the API JWT-bearer validation scheme from configuration (default section
    /// <c>Identity:JwtBearer</c>). List several authorities in <c>ValidIssuers</c> for a multi-issuer
    /// API; the token's <c>iss</c> selects the trusted authority. The principal is keyed on the
    /// issuer <c>sub</c>.
    /// </summary>
    /// <param name="builder">The authentication builder.</param>
    /// <param name="configuration">The configuration root or section parent to bind from.</param>
    /// <param name="sectionName">The configuration section to bind. Defaults to <c>Identity:JwtBearer</c>.</param>
    /// <param name="scheme">The scheme name to register. Defaults to the JWT-bearer default scheme.</param>
    /// <returns>The same builder, to enable chaining.</returns>
    /// <example>
    /// Binds from <c>Identity:JwtBearer</c> by default and resolves the signing configuration per issuer:
    /// <code>
    /// services.AddAuthentication().AddStrataraJwtBearer(configuration);
    /// </code>
    /// </example>
    public static AuthenticationBuilder AddStrataraJwtBearer(
        this AuthenticationBuilder builder,
        IConfiguration configuration,
        string sectionName = StrataraJwtBearerOptions.DefaultSectionName,
        string scheme = JwtBearerDefaults.AuthenticationScheme)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = BindJwtBearer(configuration, sectionName);
        return builder.AddJwtBearer(scheme, jwt => ApplyTo(options, jwt));
    }

    internal static StrataraOpenIdConnectOptions BindOpenIdConnect(IConfiguration configuration, string sectionName)
    {
        var options = new StrataraOpenIdConnectOptions();
        configuration.GetSection(sectionName).Bind(options);
        return options;
    }

    internal static StrataraJwtBearerOptions BindJwtBearer(IConfiguration configuration, string sectionName)
    {
        var options = new StrataraJwtBearerOptions();
        configuration.GetSection(sectionName).Bind(options);
        return options;
    }

    /// <summary>
    /// Maps the bound options onto the OpenID Connect handler. Inbound claim mapping is kept on so
    /// the issuer <c>sub</c> populates the principal's name-identifier — the value
    /// <c>ExternalLoginInfo.ProviderKey</c> is read from — because the linking invariant keys on
    /// <c>(issuer, sub)</c>, never on the mutable email claim.
    /// </summary>
    internal static void ApplyTo(StrataraOpenIdConnectOptions source, OpenIdConnectOptions target)
    {
        target.Authority = source.Authority;
        target.ClientId = source.ClientId;
        target.ClientSecret = source.ClientSecret;
        target.CallbackPath = source.CallbackPath;
        target.SignedOutCallbackPath = source.SignedOutCallbackPath;
        target.ResponseType = source.ResponseType;
        target.RequireHttpsMetadata = source.RequireHttpsMetadata;
        target.SaveTokens = source.SaveTokens;
        target.GetClaimsFromUserInfoEndpoint = source.GetClaimsFromUserInfoEndpoint;

        target.Scope.Clear();
        IEnumerable<string> scopes = source.Scopes.Count > 0 ? source.Scopes : StrataraOpenIdConnectOptions.DefaultScopes;
        foreach (var scope in scopes)
        {
            target.Scope.Add(scope);
        }

        target.MapInboundClaims = true;
    }

    /// <summary>
    /// Maps the bound options onto the JWT-bearer handler. Legacy inbound claim remapping is turned
    /// off so the raw <c>sub</c>/<c>roles</c> claim names survive and
    /// <see cref="StrataraJwtBearerOptions.NameClaimType"/>/<see cref="StrataraJwtBearerOptions.RoleClaimType"/>
    /// resolve against them.
    /// </summary>
    internal static void ApplyTo(StrataraJwtBearerOptions source, JwtBearerOptions target)
    {
        target.Authority = source.Authority;
        target.Audience = source.Audience;
        target.RequireHttpsMetadata = source.RequireHttpsMetadata;

        target.MapInboundClaims = false;
        target.TokenValidationParameters.NameClaimType = source.NameClaimType;
        target.TokenValidationParameters.RoleClaimType = source.RoleClaimType;

        if (source.ValidIssuers.Count > 0)
        {
            target.TokenValidationParameters.ValidIssuers = source.ValidIssuers;
            target.TokenValidationParameters.ValidateIssuer = true;
        }

        if (!string.IsNullOrEmpty(source.Audience))
        {
            target.TokenValidationParameters.ValidAudience = source.Audience;
            target.TokenValidationParameters.ValidateAudience = true;
        }
    }
}
