using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.ApiKeys;
using Stratara.Sessions.Multitenancy;

namespace Stratara.Identity.AspNetCore.Authentication;

/// <summary>
/// ASP.NET Core authentication handler for Stratara API keys — a standard scheme, so it
/// composes with cookie/bearer schemes via <c>[Authorize(AuthenticationSchemes=...)]</c> or the
/// Stratara auth-scheme selector.
/// </summary>
/// <remarks>
/// On success the ticket carries the name-identifier (the bound user for personal access
/// tokens, the key id for machine keys) and the <c>stratara:tenant_id</c> claim — exactly the
/// claims the session-context middleware reads, so an API-key request flows through session
/// context, membership roles, and permission resolution like any human sign-in. Validation is
/// fail-closed (unknown/revoked/expired keys fail authentication); no permissions are embedded
/// in the ticket.
/// </remarks>
/// <param name="options">The scheme options.</param>
/// <param name="logger">The ASP.NET authentication logger factory.</param>
/// <param name="encoder">URL encoder required by the base handler.</param>
/// <param name="apiKeyStore">The store validating presented keys.</param>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyStore apiKeyStore) : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    /// <inheritdoc/>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var rawKey = ResolveRawKey();
        if (rawKey is null)
        {
            return AuthenticateResult.NoResult();
        }

        var descriptor = await apiKeyStore.ValidateAsync(rawKey, Context.RequestAborted);
        if (descriptor is null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var actorId = descriptor.UserId ?? descriptor.Id;
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId.ToString("D")),
            new Claim(ClaimTypes.Name, descriptor.Name),
            new Claim(StrataraClaimTypes.TenantId, descriptor.TenantId.ToString("D")),
        ], Scheme.Name);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    private string? ResolveRawKey()
    {
        if (Request.Headers.TryGetValue(Options.HeaderName, out var headerValue)
            && !string.IsNullOrWhiteSpace(headerValue))
        {
            return headerValue.ToString();
        }

        if (Options.AllowQueryStringKey
            && Request.Query.TryGetValue(ApiKeyAuthenticationOptions.QueryParameterName, out var queryValue)
            && !string.IsNullOrWhiteSpace(queryValue))
        {
            return queryValue.ToString();
        }

        return null;
    }
}
