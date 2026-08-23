using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Validation;
using Stratara.ServiceDefaults.AspNetCore;
using Xunit;

namespace Stratara.ServiceDefaults.AspNetCore.Tests;

public class StrataraProblemDetailsExceptionHandlerTests
{
    private static readonly StrataraProblemDetailsExceptionHandler Handler = new();

    private static DefaultHttpContext ContextFor(string path = "/orders")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
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
        var context = ContextFor();

        var handled = await Handler.TryHandleAsync(context, new AuthorizationException("Admin"), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task ATenantAccessDenial_BecomesForbidden_InTheSameShape()
    {
        var context = ContextFor();

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
}
