using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Session;
using Stratara.Abstractions.Validation;
using Stratara.ServiceDefaults.AspNetCore;
using Xunit;

namespace Stratara.ServiceDefaults.AspNetCore.Tests;

public class StrataraProblemDetailsExceptionHandlerTests
{
    private static readonly StrataraProblemDetailsExceptionHandler Handler = new();

    private static DefaultHttpContext ContextFor(string path = "/orders", IServiceProvider? services = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (services is not null)
        {
            context.RequestServices = services;
        }
        return context;
    }

    private static DefaultHttpContext AuthenticatedContext()
    {
        var context = ContextFor();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "Test"));
        return context;
    }

    private static ServiceProvider ServicesWith(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure(services);
        return services.BuildServiceProvider();
    }

    private static async Task<JsonElement> BodyOf(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task AValidationFailure_BecomesBadRequest_WithTheFailuresGroupedByField()
    {
        var context = ContextFor();
        var exception = new StrataraValidationException(
        [
            new ValidationFailure("Email", "must not be empty"),
            new ValidationFailure("Email", "must be a valid address"),
            new ValidationFailure("Quantity", "must be greater than zero")
        ]);

        var handled = await Handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);

        var body = await BodyOf(context);
        var errors = body.GetProperty("errors");
        Assert.Equal(2, errors.EnumerateObject().Count());
        Assert.Equal(2, errors.GetProperty("Email").GetArrayLength());
        Assert.Equal(1, errors.GetProperty("Quantity").GetArrayLength());
    }

    [Fact]
    public async Task AValidationFailure_CarriesTheRequestPath()
    {
        var context = ContextFor("/customers/42");

        await Handler.TryHandleAsync(
            context, new StrataraValidationException([new ValidationFailure("Name", "required")]), CancellationToken.None);

        var body = await BodyOf(context);
        Assert.Equal("/customers/42", body.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task AnAuthorizationRefusal_BecomesForbidden()
    {
        var context = AuthenticatedContext();

        var handled = await Handler.TryHandleAsync(context, new AuthorizationException("Admin"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task ATenantAccessDenial_BecomesForbidden_InTheSameShape()
    {
        var context = AuthenticatedContext();

        var handled = await Handler.TryHandleAsync(
            context, new TenantAccessDeniedException(Guid.CreateVersion7(), Guid.CreateVersion7(), "tenant mismatch"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);

        var body = await BodyOf(context);
        Assert.Equal(403, body.GetProperty("status").GetInt32());
    }

    /// <summary>
    /// The negative case the change asked for explicitly: a boundary mapper that converts too much is
    /// harder to notice than one that converts too little, because the symptom is a bug reported as a
    /// tidy client error instead of reaching the host's diagnostics.
    /// </summary>
    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentNullException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(NotSupportedException))]
    public async Task AFailureTheFrameworkDidNotRaise_IsNotConverted(Type exceptionType)
    {
        var context = ContextFor();
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        var handled = await Handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    public static TheoryData<string> MissingIdentityFailures() =>
    [
        nameof(SessionRequiredException),
        nameof(AuthorizationException),
        nameof(TenantAccessDeniedException)
    ];

    private static Exception MissingIdentityFailure(string kind) => kind switch
    {
        nameof(SessionRequiredException) => new SessionRequiredException("Session context is not set"),
        nameof(AuthorizationException) => new AuthorizationException("Admin"),
        _ => new TenantAccessDeniedException(Guid.CreateVersion7(), Guid.Empty, "no session")
    };

    [Theory]
    [MemberData(nameof(MissingIdentityFailures))]
    public async Task AnAnonymousCaller_WithoutAScheme_IsAnsweredUnauthorized_InTheProblemShape(string kind)
    {
        var exception = MissingIdentityFailure(kind);
        var context = ContextFor();

        var handled = await Handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        var body = await BodyOf(context);
        Assert.Equal(401, body.GetProperty("status").GetInt32());
        Assert.Equal("/orders", body.GetProperty("instance").GetString());
    }

    [Fact]
    public async Task AnAnonymousCaller_WithAnAuthenticationServiceButNoScheme_IsAnsweredUnauthorized()
    {
        await using var services = ServicesWith(collection => collection.AddAuthentication());
        var context = ContextFor(services: services);

        var handled = await Handler.TryHandleAsync(context, new SessionRequiredException(), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal(401, (await BodyOf(context)).GetProperty("status").GetInt32());
    }

    [Theory]
    [MemberData(nameof(MissingIdentityFailures))]
    public async Task AnAnonymousCaller_WithABearerScheme_IsChallenged_AndReceivesTheProblemBody(string kind)
    {
        var exception = MissingIdentityFailure(kind);
        await using var services = ServicesWith(collection =>
            collection.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer());
        var context = ContextFor(services: services);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var handled = await Handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.StartsWith("Bearer", context.Response.Headers[HeaderNames.WWWAuthenticate].ToString(), StringComparison.Ordinal);
        Assert.Equal(401, (await BodyOf(context)).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task AnAnonymousCaller_WithACookieScheme_IsRedirected_AndNoProblemBodyIsWritten()
    {
        await using var services = ServicesWith(collection =>
            collection.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie());
        var context = ContextFor(services: services);
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.test");

        var handled = await Handler.TryHandleAsync(context, new SessionRequiredException(), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Contains("/Account/Login", context.Response.Headers[HeaderNames.Location].ToString(), StringComparison.Ordinal);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task AnAuthenticatedCaller_WithoutASession_IsNotConverted()
    {
        var context = AuthenticatedContext();

        var handled = await Handler.TryHandleAsync(context, new SessionRequiredException(), CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task AnAnonymousCallersValidationFailure_StillBecomesBadRequest()
    {
        var context = ContextFor();

        var handled = await Handler.TryHandleAsync(
            context, new StrataraValidationException([new ValidationFailure("Name", "required")]), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public void SessionRequiredException_IsAnInvalidOperationException_KeepingItsMessage()
    {
        InvalidOperationException exception = new SessionRequiredException("Session context is not set");

        Assert.Equal("Session context is not set", exception.Message);
    }
}
