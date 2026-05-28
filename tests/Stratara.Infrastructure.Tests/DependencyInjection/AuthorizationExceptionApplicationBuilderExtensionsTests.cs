using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Authorization;

namespace Stratara.Infrastructure.Tests.DependencyInjection;

public class AuthorizationExceptionApplicationBuilderExtensionsTests
{
    [Fact]
    public async Task UseAuthorizationExceptionTo403_AuthorizationException_Returns403()
    {
        var pipeline = BuildPipeline(_ => throw new AuthorizationException("forbidden"));

        var httpContext = new DefaultHttpContext();
        await pipeline(httpContext);

        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task UseAuthorizationExceptionTo403_NoException_LeavesDefaultStatus()
    {
        var pipeline = BuildPipeline(_ => Task.CompletedTask);

        var httpContext = new DefaultHttpContext();
        await pipeline(httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task UseAuthorizationExceptionTo403_OtherException_Rethrows()
    {
        var pipeline = BuildPipeline(_ => throw new InvalidOperationException("other"));

        var httpContext = new DefaultHttpContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline(httpContext));
    }

    [Fact]
    public void UseAuthorizationExceptionTo403_NullApp_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ((IApplicationBuilder)null!).UseAuthorizationExceptionTo403());
    }

    [Fact]
    public void UseAuthorizationExceptionTo403_ReturnsSameBuilderForChaining()
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());

        var result = app.UseAuthorizationExceptionTo403();

        Assert.Same(app, result);
    }

    private static RequestDelegate BuildPipeline(RequestDelegate terminal)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseAuthorizationExceptionTo403();
        app.Run(terminal);
        return app.Build();
    }
}
