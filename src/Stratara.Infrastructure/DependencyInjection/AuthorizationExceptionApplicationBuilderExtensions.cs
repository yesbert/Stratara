using Microsoft.AspNetCore.Builder;
using Stratara.Infrastructure.Middlewares;

// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Application-builder extensions for wiring the Stratara
/// <see cref="AuthorizationExceptionMiddleware"/> into an ASP.NET Core request pipeline.
/// </summary>
public static class AuthorizationExceptionApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the <see cref="AuthorizationExceptionMiddleware"/> to the request pipeline so that any
    /// <see cref="Stratara.Abstractions.Authorization.AuthorizationException"/> thrown downstream
    /// (e.g. by <c>AuthorizingMediator</c> or <c>AuthorizingCommandOutboxDispatcher</c>) is
    /// translated into an HTTP 403 (Forbidden) response. Other exceptions are not caught and
    /// continue to propagate to the outer pipeline / global exception handler.
    /// </summary>
    /// <remarks>
    /// Wire this middleware close to the top of the request pipeline — after authentication and
    /// authorization, before any application-specific middleware that dispatches commands or
    /// queries through the Stratara mediator.
    /// </remarks>
    /// <param name="app">The application builder.</param>
    /// <returns>The same application builder, to enable chaining.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="app"/> is null.</exception>
    public static IApplicationBuilder UseAuthorizationExceptionTo403(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseMiddleware<AuthorizationExceptionMiddleware>();
    }
}
