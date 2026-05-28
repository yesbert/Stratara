using Microsoft.AspNetCore.Http;
using Stratara.Infrastructure.Middlewares;
using Stratara.Abstractions.Authorization;

namespace Stratara.Infrastructure.Tests.Middlewares;

public class AuthorizationExceptionMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_NoException_CallsNext()
    {
        var nextCalled = false;
        var middleware = new AuthorizationExceptionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var httpContext = new DefaultHttpContext();

        await middleware.InvokeAsync(httpContext);

        Assert.True(nextCalled);
        Assert.Equal(200, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_AuthorizationException_Returns403()
    {
        var middleware = new AuthorizationExceptionMiddleware(_ =>
            throw new AuthorizationException("forbidden"));

        var httpContext = new DefaultHttpContext();

        await middleware.InvokeAsync(httpContext);

        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_OtherException_Rethrows()
    {
        var middleware = new AuthorizationExceptionMiddleware(_ =>
            throw new InvalidOperationException("other error"));

        var httpContext = new DefaultHttpContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(httpContext));
    }
}
