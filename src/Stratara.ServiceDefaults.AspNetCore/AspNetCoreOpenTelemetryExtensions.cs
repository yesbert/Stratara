using System.Diagnostics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// ASP.NET-specific extensions to the base OpenTelemetry pipeline from <c>Stratara.ServiceDefaults</c>.
/// Adds request instrumentation and excludes the health-check endpoints from traces.
/// </summary>
/// <remarks>
/// Server-side ASP.NET Core instrumentation registers an enrichment callback that overwrites any
/// captured <c>http.request.header.authorization</c> / <c>http.request.header.cookie</c> /
/// <c>http.response.header.set-cookie</c> tags with <c>"REDACTED"</c>. This is a defensive measure
/// for hosts that opt into header capture via <c>OTEL_INSTRUMENTATION_HTTP_CAPTURE_HEADERS</c> or
/// the SDK's own options — without that opt-in, OTel does not record headers anyway, so this is
/// a belt-and-braces safeguard against incoming bearer tokens or session cookies leaking through
/// trace exporters.
/// </remarks>
public static class AspNetCoreOpenTelemetryExtensions
{
    private static readonly string[] SensitiveHeaderTagNames =
    [
        "http.request.header.authorization",
        "http.request.header.cookie",
        "http.request.header.proxy_authorization",
        "http.response.header.set_cookie",
    ];

    private const string RedactedValue = "REDACTED";

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Minor Code Smell",
        "S3267:Loops should be simplified with \"LINQ\" expressions",
        Justification = "Hot path on every captured HTTP request. The for-loop avoids the LINQ-Where enumerator + delegate allocations per request — measurable Gen0 garbage in high-RPS hosts.")]
    private static void RedactSensitiveHeaderTags(Activity activity)
    {
        for (var i = 0; i < SensitiveHeaderTagNames.Length; i++)
        {
            var tag = SensitiveHeaderTagNames[i];
            if (activity.GetTagItem(tag) is not null)
            {
                activity.SetTag(tag, RedactedValue);
            }
        }
    }

    /// <summary>
    /// Wires ASP.NET Core request instrumentation on metrics + tracing on top of the base pipeline configured
    /// by <c>ConfigureOpenTelemetry</c>. Filters out the health (<c>/health</c>) and aliveness (<c>/alive</c>)
    /// endpoints from tracing so they don't pollute the request stream.
    /// </summary>
    /// <typeparam name="TBuilder">The host-builder type.</typeparam>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder for chaining.</returns>
    public static TBuilder ConfigureAspNetOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry(metrics => { metrics.AddAspNetCoreInstrumentation(); },
            tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments(Endpoints.HealthEndpointPath) &&
                            !context.Request.Path.StartsWithSegments(Endpoints.AlivenessEndpointPath);
                        options.EnrichWithHttpRequest = (activity, _) => RedactSensitiveHeaderTags(activity);
                        options.EnrichWithHttpResponse = (activity, _) => RedactSensitiveHeaderTags(activity);
                    });
            });

        return builder;
    }
}
