using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Validation;

namespace Stratara.ServiceDefaults.AspNetCore;

/// <summary>
/// Maps the framework's own failure types to an RFC 7807 problem response, so a caller receives one
/// shape for every framework rejection instead of one shape per failure type.
/// </summary>
/// <remarks>
/// <para>
/// Handles exactly three types: a validation rejection becomes <c>400</c> carrying the failures
/// grouped by the field each concerns; an authorization refusal and a tenant-access denial each
/// become <c>403</c>. Everything else is left alone and propagates unchanged — a host with its own
/// error model keeps it, and a bug in a handler still reaches the host's own diagnostics rather than
/// being flattened into a tidy 500.
/// </para>
/// <para>
/// Registered by <c>AddStrataraProblemDetails()</c>; a host that does not call it converts nothing.
/// </para>
/// </remarks>
public sealed class StrataraProblemDetailsExceptionHandler : IExceptionHandler
{
    /// <inheritdoc/>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var problem = Describe(exception, httpContext);
        if (problem is null)
        {
            return false;
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json", cancellationToken);

        return true;
    }

    private static ProblemDetails? Describe(Exception exception, HttpContext httpContext) => exception switch
    {
        StrataraValidationException validation => new ValidationProblemDetails(GroupByField(validation))
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation failures occurred.",
            Type = "https://datatracker.ietf.org/doc/html/rfc9110#name-400-bad-request",
            Instance = httpContext.Request.Path
        },
        AuthorizationException or TenantAccessDeniedException => new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "The request was refused.",
            Type = "https://datatracker.ietf.org/doc/html/rfc9110#name-403-forbidden",
            Instance = httpContext.Request.Path
        },
        _ => null
    };

    /// <summary>
    /// Groups the failures by the property each concerns. The exception carries a flat list, and a
    /// single field can fail several rules at once, so grouping is what lets a caller attribute
    /// every message rather than guess.
    /// </summary>
    private static Dictionary<string, string[]> GroupByField(StrataraValidationException validation) =>
        validation.Failures
            .GroupBy(failure => failure.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(failure => failure.ErrorMessage).ToArray(),
                StringComparer.Ordinal);
}
