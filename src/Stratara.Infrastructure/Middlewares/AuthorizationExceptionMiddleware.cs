using Microsoft.AspNetCore.Http;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Multitenancy;

namespace Stratara.Infrastructure.Middlewares;

/// <summary>
/// ASP.NET Core middleware that translates an <see cref="AuthorizationException"/> or
/// <see cref="TenantAccessDeniedException"/> thrown anywhere downstream into an HTTP 403 (Forbidden)
/// response.
/// </summary>
/// <remarks>
/// Other exceptions are not caught and propagate to the outer pipeline / global exception handler.
/// Wire this middleware up close to the top of the request pipeline so role checks emitted by
/// <c>AuthorizingMediator</c> / <see cref="Stratara.Infrastructure.Authorization.AuthorizingCommandOutboxDispatcher"/>
/// and tenant-isolation rejections from the mediator pipeline surface as 403 rather than 500.
/// </remarks>
internal sealed class AuthorizationExceptionMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Invokes the next middleware and converts an <see cref="AuthorizationException"/> or
    /// <see cref="TenantAccessDeniedException"/> into a 403 response.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    public async Task InvokeAsync(HttpContext httpContext)
    {
        try
        {
            await next(httpContext);
        }
        catch (Exception ex) when (ex is AuthorizationException or TenantAccessDeniedException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
    }
}
