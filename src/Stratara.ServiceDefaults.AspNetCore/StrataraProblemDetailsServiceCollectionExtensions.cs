using Microsoft.Extensions.DependencyInjection;
using Stratara.ServiceDefaults.AspNetCore;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the mapping from Stratara's own failure types to RFC 7807 problem responses.
/// </summary>
public static class StrataraProblemDetailsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="StrataraProblemDetailsExceptionHandler"/> and the ASP.NET problem-details
    /// service, so a validation rejection becomes <c>400</c> with the failures grouped by field, and
    /// an authorization refusal or tenant-access denial becomes <c>403</c> — all in one shape.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mapping is opt-in and converts nothing a host does not ask it to: any failure the
    /// framework did not raise propagates unchanged, so a host keeping its own error model simply
    /// does not call this.
    /// </para>
    /// <para>
    /// Call <c>app.UseExceptionHandler()</c> in the pipeline for the handler to run. Without it the
    /// handler is registered but never reached, and failures propagate as if the mapping were absent.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <example>
    /// One RFC 7807 shape for a validation rejection (400 with the failures grouped by field) and for an
    /// authorization or tenant-access refusal (403). Pair it with the exception handler:
    /// <code>
    /// services.AddStrataraProblemDetails();
    /// app.UseExceptionHandler();
    /// </code>
    /// </example>
    public static IServiceCollection AddStrataraProblemDetails(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProblemDetails();
        services.AddExceptionHandler<StrataraProblemDetailsExceptionHandler>();

        return services;
    }
}
