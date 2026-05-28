using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Stratara.Diagnostics;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// OpenTelemetry wiring used by every Stratara host. Configures logging + metrics + tracing with HTTP,
/// EF Core, RabbitMQ, and runtime instrumentation, and registers an OTLP exporter automatically when
/// the <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> configuration value is non-empty.
/// </summary>
/// <remarks>
/// <para>
/// When the OTLP exporter is enabled, the export timeout (<c>OTEL_EXPORTER_OTLP_TIMEOUT</c>) defaults
/// to 5000 ms instead of the OTel spec default of 10 000 ms to keep host shutdown / metric flush
/// reactive when the collector is unreachable. Consumers can override either default via the same
/// environment variable or configuration key.
/// </para>
/// <para>
/// HTTP-client instrumentation registers an enrichment callback that overwrites any
/// <c>http.request.header.authorization</c> / <c>http.request.header.cookie</c> /
/// <c>http.response.header.set-cookie</c> tags with <c>"REDACTED"</c>. This defends against bearer
/// tokens or session cookies leaking into trace exporters when a consumer opts into header capture
/// (via <c>OTEL_INSTRUMENTATION_HTTP_CAPTURE_HEADERS</c> or the SDK's own options). The default OTel
/// behaviour without that opt-in is already to omit headers, so for most consumers this is a
/// belt-and-braces safeguard.
/// </para>
/// </remarks>
public static class OpenTelemetryExtensions
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
    private static void RedactSensitiveHeaderTags(System.Diagnostics.Activity activity)
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
    /// Configures OpenTelemetry logging + metrics + tracing on the given host builder.
    /// Adds default instrumentation (HTTP client, EF Core, RabbitMQ, runtime) and wires the OTLP exporter
    /// when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
    /// </summary>
    /// <typeparam name="TBuilder">The host-builder type.</typeparam>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configureMetrics">Optional callback to extend the metrics pipeline with host-specific meters/exporters.</param>
    /// <param name="configureTracing">Optional callback to extend the tracing pipeline with host-specific sources/exporters.</param>
    /// <returns>The same builder for chaining.</returns>
    public static TBuilder ConfigureOpenTelemetry<TBuilder>(
        this TBuilder builder,
        Action<MeterProviderBuilder>? configureMetrics = null,
        Action<TracerProviderBuilder>? configureTracing = null)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter("Stratara.Service");

                configureMetrics?.Invoke(metrics);
            })
            .WithTracing(tracing =>
            {
                if (builder.Environment.IsDevelopment()) tracing.SetSampler(new AlwaysOnSampler());

                tracing
                    .AddSource(builder.Environment.ApplicationName, ApplicationDiagnostics.Activity.SourceName)
                    .AddHttpClientInstrumentation(options =>
                    {
                        options.EnrichWithHttpRequestMessage = (activity, _) => RedactSensitiveHeaderTags(activity);
                        options.EnrichWithHttpResponseMessage = (activity, _) => RedactSensitiveHeaderTags(activity);
                    })
                    .AddRabbitMQInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation(options =>
                    {
                        options.EnrichWithIDbCommand = (activity, command) =>
                        {
                            activity.SetTag("db.command_timeout", command.CommandTimeout);
                        };
                    });

                configureTracing?.Invoke(tracing);
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string OtlpTimeoutKey = "OTEL_EXPORTER_OTLP_TIMEOUT";
    private const string DefaultOtlpTimeoutMilliseconds = "5000";

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointKey])) return builder;

        if (string.IsNullOrWhiteSpace(builder.Configuration[OtlpTimeoutKey])
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OtlpTimeoutKey)))
        {
            Environment.SetEnvironmentVariable(OtlpTimeoutKey, DefaultOtlpTimeoutMilliseconds);
        }

        builder.Services.AddOpenTelemetry().UseOtlpExporter();

        return builder;
    }
}
