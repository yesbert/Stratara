using System.Security.Claims;
using Stratara.Contracts.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Session;
using Stratara.Sessions.Multitenancy;

namespace Stratara.Sessions.Middlewares;

/// <summary>
/// ASP.NET Core middleware that populates the ambient <see cref="ISessionContextProvider"/>
/// from the incoming request's JWT claims. For unauthenticated requests the middleware
/// leaves the provider untouched.
/// </summary>
/// <remarks>
/// Default behaviour is Actor = Subject — UserPlatform users operate on their own
/// tenant's data. PlatformAdmin endpoints that act on a foreign tenant override
/// <see cref="SessionContext.TenantId"/> after authorization.
/// <para>
/// The <c>X-Tenant-Id</c> HTTP header is honoured as a fallback for the tenant claim
/// <strong>only when</strong> <see cref="SessionContextOptions.AllowTenantHeader"/> is set
/// to <see langword="true"/>. The default since 3.0.10 is fail-closed: a missing or
/// unparsable claim resolves to <see cref="DefaultTenantIdentifier.Value"/> regardless of
/// the header. See Round-3-Audit Finding KI-04 for the threat model.
/// </para>
/// </remarks>
public sealed class SessionContextMiddleware(RequestDelegate next, IOptions<SessionContextOptions> options)
{
    private readonly SessionContextOptions _options = options.Value;

    /// <summary>Process a single request: parse identity claims and set the session context.</summary>
    /// <param name="httpContext">The current request's <see cref="HttpContext"/>.</param>
    /// <param name="sessionContextProvider">The ambient session-context provider (DI-resolved per request).</param>
    public async Task InvokeAsync(HttpContext httpContext, ISessionContextProvider sessionContextProvider)
    {
        var correlationId = httpContext.TraceIdentifier is { Length: > 0 }
            ? httpContext.TraceIdentifier
            : Guid.CreateVersion7().ToString("N");

        if (httpContext.User.Identity?.IsAuthenticated == false)
        {
            await next(httpContext);
            return;
        }

        var nameIdentifier = httpContext.User.FindFirst(ClaimTypes.NameIdentifier);
        var userId = nameIdentifier is not null && Guid.TryParse(nameIdentifier.Value, out var parsedUserId)
            ? parsedUserId
            : Guid.Empty;

        var tenantId = ResolveTenantId(httpContext);
        var clientId = ResolveClientId(httpContext);

        var context = new SessionContext(
            correlationId,
            null,
            null,
            tenantId,
            userId,
            tenantId,
            null,
            clientId
        );

        sessionContextProvider.Set(context);

        await next(httpContext);
    }

    private Guid ResolveTenantId(HttpContext httpContext)
    {
        var tenantIdClaim = httpContext.User.FindFirst(StrataraClaimTypes.TenantId);
        if (tenantIdClaim is not null && Guid.TryParse(tenantIdClaim.Value, out var claimTenantId))
        {
            return claimTenantId;
        }

        if (!_options.AllowTenantHeader)
        {
            return DefaultTenantIdentifier.Value;
        }

        var headerTenant = httpContext.Request.Headers[StrataraHeaderNames.TenantId].ToString();
        return Guid.TryParse(headerTenant, out var headerTenantId) ? headerTenantId : DefaultTenantIdentifier.Value;
    }

    private static Guid? ResolveClientId(HttpContext httpContext)
    {
        var clientIdHeader = httpContext.Request.Headers[StrataraHeaderNames.ClientId].ToString();
        return !string.IsNullOrEmpty(clientIdHeader) && Guid.TryParse(clientIdHeader, out var clientId)
            ? clientId
            : null;
    }
}
