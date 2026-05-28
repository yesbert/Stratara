using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Stratara.Contracts.Session;
using Stratara.Sessions;
using Stratara.Sessions.Middlewares;
using Stratara.Abstractions.Session;
using Stratara.Sessions.Multitenancy;

namespace Stratara.Infrastructure.Tests.Middlewares;

public class SessionContextMiddlewareTests
{
    private readonly Mock<ISessionContextProvider> _sessionContextProviderMock = new();
    private SessionContext? _capturedContext;

    public SessionContextMiddlewareTests()
    {
        _sessionContextProviderMock
            .Setup(p => p.Set(It.IsAny<SessionContext>()))
            .Callback<SessionContext>(ctx => _capturedContext = ctx);
    }

    [Fact]
    public async Task InvokeAsync_UnauthenticatedUser_SkipsContextSetup()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(allowTenantHeader: false, next: _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var httpContext = new DefaultHttpContext();

        await middleware.InvokeAsync(httpContext, _sessionContextProviderMock.Object);

        Assert.True(nextCalled);
        _sessionContextProviderMock.Verify(p => p.Set(It.IsAny<SessionContext>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedUser_SetsSessionContext()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(allowTenantHeader: false, next: _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var httpContext = CreateAuthenticatedContext(userId, tenantId);

        await middleware.InvokeAsync(httpContext, _sessionContextProviderMock.Object);

        Assert.True(nextCalled);
        Assert.NotNull(_capturedContext);
        Assert.Equal(userId, _capturedContext!.ActorUserId);
        Assert.Equal(tenantId, _capturedContext.TenantId);
    }

    [Fact]
    public async Task InvokeAsync_AuthenticatedUser_UsesTraceIdentifierAsCorrelationId()
    {
        var middleware = CreateMiddleware();
        var httpContext = CreateAuthenticatedContext(Guid.NewGuid(), Guid.NewGuid());
        httpContext.TraceIdentifier = "test-trace-id";

        await middleware.InvokeAsync(httpContext, _sessionContextProviderMock.Object);

        Assert.Equal("test-trace-id", _capturedContext!.CorrelationId);
    }

    [Fact]
    public async Task InvokeAsync_EmptyTraceIdentifier_GeneratesCorrelationId()
    {
        var middleware = CreateMiddleware();
        var httpContext = CreateAuthenticatedContext(Guid.NewGuid(), Guid.NewGuid());
        httpContext.TraceIdentifier = "";

        await middleware.InvokeAsync(httpContext, _sessionContextProviderMock.Object);

        Assert.NotNull(_capturedContext);
        Assert.False(string.IsNullOrEmpty(_capturedContext!.CorrelationId));
    }

    [Fact]
    public async Task InvokeAsync_NoNameIdentifier_UsesEmptyGuidForUserId()
    {
        var middleware = CreateMiddleware();

        var claims = new List<Claim>
        {
            new(StrataraClaimTypes.TenantId, Guid.NewGuid().ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };

        await middleware.InvokeAsync(httpContext, _sessionContextProviderMock.Object);

        Assert.Equal(Guid.Empty, _capturedContext!.ActorUserId);
    }

    [Fact]
    public async Task InvokeAsync_TenantIdFromClaim_UsesClaim()
    {
        var middleware = CreateMiddleware();
        var tenantId = Guid.NewGuid();
        var httpContext = CreateAuthenticatedContext(Guid.NewGuid(), tenantId);

        await middleware.InvokeAsync(httpContext, _sessionContextProviderMock.Object);

        Assert.Equal(tenantId, _capturedContext!.TenantId);
    }

    [Fact]
    public async Task InvokeAsync_TenantHeader_AllowedByOptions_FallsBackToHeader()
    {
        var middleware = CreateMiddleware(allowTenantHeader: true);
        var tenantId = Guid.NewGuid();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        httpContext.Request.Headers[StrataraHeaderNames.TenantId] = tenantId.ToString();

        await middleware.InvokeAsync(httpContext, _sessionContextProviderMock.Object);

        Assert.Equal(tenantId, _capturedContext!.TenantId);
    }

    [Fact]
    public async Task InvokeAsync_TenantHeader_NotAllowedByOptions_UsesDefault()
    {
        var middleware = CreateMiddleware(allowTenantHeader: false);
        var headerTenantId = Guid.NewGuid();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        httpContext.Request.Headers[StrataraHeaderNames.TenantId] = headerTenantId.ToString();

        await middleware.InvokeAsync(httpContext, _sessionContextProviderMock.Object);

        Assert.Equal(DefaultTenantIdentifier.Value, _capturedContext!.TenantId);
        Assert.NotEqual(headerTenantId, _capturedContext.TenantId);
    }

    [Fact]
    public async Task InvokeAsync_NoTenantIdAnywhere_UsesDefaultTenantIdentifier()
    {
        var middleware = CreateMiddleware();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };

        await middleware.InvokeAsync(httpContext, _sessionContextProviderMock.Object);

        Assert.Equal(DefaultTenantIdentifier.Value, _capturedContext!.TenantId);
    }

    [Fact]
    public async Task InvokeAsync_ClientIdHeader_SetsClientId()
    {
        var middleware = CreateMiddleware();
        var clientId = Guid.NewGuid();
        var httpContext = CreateAuthenticatedContext(Guid.NewGuid(), Guid.NewGuid());
        httpContext.Request.Headers[StrataraHeaderNames.ClientId] = clientId.ToString();

        await middleware.InvokeAsync(httpContext, _sessionContextProviderMock.Object);

        Assert.Equal(clientId, _capturedContext!.ClientId);
    }

    [Fact]
    public async Task InvokeAsync_NoClientIdHeader_ClientIdIsNull()
    {
        var middleware = CreateMiddleware();
        var httpContext = CreateAuthenticatedContext(Guid.NewGuid(), Guid.NewGuid());

        await middleware.InvokeAsync(httpContext, _sessionContextProviderMock.Object);

        Assert.Null(_capturedContext!.ClientId);
    }

    [Fact]
    public async Task InvokeAsync_InvalidClientIdHeader_ClientIdIsNull()
    {
        var middleware = CreateMiddleware();
        var httpContext = CreateAuthenticatedContext(Guid.NewGuid(), Guid.NewGuid());
        httpContext.Request.Headers[StrataraHeaderNames.ClientId] = "not-a-guid";

        await middleware.InvokeAsync(httpContext, _sessionContextProviderMock.Object);

        Assert.Null(_capturedContext!.ClientId);
    }

    private static SessionContextMiddleware CreateMiddleware(bool allowTenantHeader = false, RequestDelegate? next = null) =>
        new(next ?? (_ => Task.CompletedTask),
            Options.Create(new SessionContextOptions { AllowTenantHeader = allowTenantHeader }));

    private static DefaultHttpContext CreateAuthenticatedContext(Guid userId, Guid tenantId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(StrataraClaimTypes.TenantId, tenantId.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }
}
