using Microsoft.AspNetCore.Http;
using Stratara.Abstractions.Authorization;

namespace Stratara.Infrastructure.Middlewares;

/// <summary>
/// ASP.NET Core middleware that translates <see cref="AuthorizationException"/> thrown anywhere
/// downstream into an HTTP 403 (Forbidden) response.
/// </summary>
/// <remarks>
/// Other exceptions are not caught and propagate to the outer pipeline / global exception handler.
/// Wire this middleware up close to the top of the request pipeline so role checks emitted by
/// <c>AuthorizingMediator</c> / <see cref="Stratara.Infrastructure.Authorization.AuthorizingCommandOutboxDispatcher"/>
/// surface as 403 rather than 500.
/// </remarks>
internal sealed class AuthorizationExceptionMiddleware(RequestDelegate next)
{
    /// <summary>Invokes the next middleware and converts <see cref="AuthorizationException"/> into a 403 response.</summary>
    /// <param name="httpContext">The current HTTP context.</param>
    public async Task InvokeAsync(HttpContext httpContext)
    {
        try
        {
            await next(httpContext);
        }
        catch (AuthorizationException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
    }
}
