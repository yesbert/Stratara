using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Authorization;
using Stratara.Abstractions.Multitenancy;
using Stratara.Abstractions.Session;
using Stratara.Abstractions.Validation;

namespace Stratara.ServiceDefaults.AspNetCore;

/// <summary>
/// Maps the framework's own failure types to an RFC 7807 problem response, so a caller receives one
/// shape for every framework rejection instead of one shape per failure type.
/// </summary>
/// <remarks>
/// <para>
/// Handles exactly four types: a validation rejection becomes <c>400</c> carrying the failures
/// grouped by the field each concerns; an authorization refusal and a tenant-access denial each
/// become <c>403</c>. Everything else is left alone and propagates unchanged — a host with its own
/// error model keeps it, and a bug in a handler still reaches the host's own diagnostics rather than
/// being flattened into a tidy 500.
/// </para>
/// <para>
/// A caller that is not authenticated is answered <c>401</c> instead, for an authorization refusal,
/// a tenant-access denial and a <see cref="SessionRequiredException"/> alike: what such a caller lacks
/// is an identity, not a permission. Where the host has a default challenge scheme the handler
/// challenges through it — so a bearer client receives <c>WWW-Authenticate</c> and a cookie client is
/// redirected, as <c>[Authorize]</c> would do — and writes the problem body only when the challenge
/// leaves an unstarted <c>401</c>. Without a scheme it writes a <c>401</c> problem response itself.
/// A <see cref="SessionRequiredException"/> for an authenticated caller is not converted: it means
/// the host did not establish the session context, and that stays loud.
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

        if (IsMissingIdentity(exception) && httpContext.User.Identity?.IsAuthenticated != true)
        {
            await AnswerUnauthenticatedAsync(httpContext, cancellationToken);
            return true;
        }

        var problem = Describe(exception, httpContext);
        if (problem is null)
        {
            return false;
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await WriteAsync(httpContext, problem, cancellationToken);

        return true;
    }

    private static bool IsMissingIdentity(Exception exception) =>
        exception is SessionRequiredException or AuthorizationException or TenantAccessDeniedException;

    private static async Task AnswerUnauthenticatedAsync(HttpContext httpContext, CancellationToken cancellationToken)
    {
        var schemes = httpContext.RequestServices?.GetService<IAuthenticationSchemeProvider>();
        var challengeScheme = schemes is null ? null : await schemes.GetDefaultChallengeSchemeAsync();
        if (challengeScheme is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
        else
        {
            await httpContext.ChallengeAsync();
            if (httpContext.Response.HasStarted || httpContext.Response.StatusCode != StatusCodes.Status401Unauthorized)
            {
                return;
            }
        }

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "The request requires an authenticated caller.",
            Type = "https://datatracker.ietf.org/doc/html/rfc9110#name-401-unauthorized",
            Instance = httpContext.Request.Path
        };
        await WriteAsync(httpContext, problem, cancellationToken);
    }

    private static Task WriteAsync(HttpContext httpContext, ProblemDetails problem, CancellationToken cancellationToken) =>
        httpContext.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json", cancellationToken);

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
