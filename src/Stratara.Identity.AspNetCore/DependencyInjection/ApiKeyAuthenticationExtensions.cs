using Microsoft.AspNetCore.Authentication;
using Stratara.Identity.AspNetCore.Authentication;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Authentication-builder extensions for the Stratara API-key scheme and the auth-scheme
/// selector. API keys are just another ASP.NET Core scheme — the provider seam is the scheme,
/// not a custom abstraction — so they compose with cookies, bearer tokens, and OIDC handlers.
/// </summary>
public static class ApiKeyAuthenticationExtensions
{
    /// <summary>
    /// Register the Stratara API-key authentication scheme
    /// (<see cref="ApiKeyAuthenticationOptions.SchemeName"/>). Requires a registered
    /// <see cref="Stratara.Abstractions.ApiKeys.IApiKeyStore"/> (for example
    /// <c>AddApiKeyStore&lt;TContext&gt;()</c> from Stratara.Identity.EntityFrameworkCore).
    /// </summary>
    /// <param name="builder">The authentication builder.</param>
    /// <param name="configure">Optional callback to configure header/query behavior.</param>
    /// <returns>The same builder, to enable chaining.</returns>
    /// <example>
    /// Reads the <c>X-Api-Key</c> header; the query-parameter fallback is opt-in:
    /// <code>
    /// services.AddAuthentication().AddStrataraApiKey();
    /// </code>
    /// </example>
    public static AuthenticationBuilder AddStrataraApiKey(
        this AuthenticationBuilder builder,
        Action<ApiKeyAuthenticationOptions>? configure = null)
    {
        return builder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationOptions.SchemeName, configure);
    }

    /// <summary>
    /// Register the Stratara auth-scheme selector
    /// (<see cref="StrataraAuthSchemeSelectorOptions.SchemeName"/>): a policy scheme that routes
    /// each request by shape — API-key header → API-key scheme, <c>Authorization: Bearer</c> →
    /// bearer scheme, everything else → the fallback (cookie) scheme. Set it as the default
    /// scheme so mixed API/browser hosts need no per-endpoint scheme lists.
    /// </summary>
    /// <param name="builder">The authentication builder.</param>
    /// <param name="configure">Optional callback to override the routed scheme names.</param>
    /// <returns>The same builder, to enable chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddAuthentication(StrataraAuthSchemeSelectorOptions.SchemeName)
    ///     .AddStrataraApiKey()
    ///     .AddStrataraAuthSchemeSelector();
    /// </code>
    /// </example>
    public static AuthenticationBuilder AddStrataraAuthSchemeSelector(
        this AuthenticationBuilder builder,
        Action<StrataraAuthSchemeSelectorOptions>? configure = null)
    {
        var options = new StrataraAuthSchemeSelectorOptions();
        configure?.Invoke(options);

        return builder.AddPolicyScheme(
            StrataraAuthSchemeSelectorOptions.SchemeName,
            StrataraAuthSchemeSelectorOptions.SchemeName,
            policy => policy.ForwardDefaultSelector = context =>
            {
                if (context.Request.Headers.ContainsKey(options.ApiKeyHeaderName))
                {
                    return options.ApiKeyScheme;
                }

                var authorization = context.Request.Headers.Authorization.ToString();
                return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? options.BearerScheme
                    : options.FallbackScheme;
            });
    }
}
