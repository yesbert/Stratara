using Destructurama;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.OpenTelemetry;
using Serilog.Sinks.SystemConsole.Themes;
using ILogger = Serilog.ILogger;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Serilog wiring used by every Stratara host. Configures structured logging with destructuring attributes,
/// async console sink, OTLP sink (when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set), and dev-mode log-file
/// cleanup at <c>{Path.GetTempPath()}/stratara-logs/{service-name}.log</c>.
/// </summary>
public static class SerilogExtensions
{
    /// <summary>
    /// Registers Serilog as the host's logging provider with Stratara defaults (destructuring attributes, async
    /// console sink, OTLP sink when configured). Reads additional configuration from <c>appsettings.json</c>
    /// (Serilog section) and respects <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> / <c>OTEL_EXPORTER_OTLP_PROTOCOL</c>
    /// / <c>OTEL_SERVICE_NAME</c> environment variables.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The same builder for chaining.</returns>
    public static IHostApplicationBuilder ConfigureSerilog(this IHostApplicationBuilder builder)
    {
        var otlpExporter = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var otlpProtocol = builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"];
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "Unknown";
        Log.Logger.Information("App Service name {Name}", serviceName);

        if (builder.Environment.IsDevelopment())
        {
            CleanupDevelopmentLogs(serviceName);
        }

        builder.Logging.Configure(options =>
        {
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId |
                ActivityTrackingOptions.SpanId |
                ActivityTrackingOptions.Tags |
                ActivityTrackingOptions.Baggage;
        });

        builder.Services.AddSerilog((_, loggerConfiguration) =>
        {
            loggerConfiguration
                .AddDefaultLoggerConfiguration()
                .ReadFrom.Configuration(builder.Configuration);

            if (!string.IsNullOrEmpty(otlpExporter))
            {
                loggerConfiguration
                    .WriteTo.OpenTelemetry(options =>
                    {
                        options.Endpoint = otlpExporter;
                        options.Protocol = string.Equals(otlpProtocol, "grpc", StringComparison.OrdinalIgnoreCase)
                            ? OtlpProtocol.Grpc
                            : OtlpProtocol.HttpProtobuf;
                        options.ResourceAttributes.Add("service.name", serviceName);
                    });
            }
        });

        builder.Logging.ClearProviders().AddSerilog();
        return builder;
    }

    /// <summary>
    /// Installs a Serilog bootstrap logger as the global <see cref="Log.Logger"/>, so messages emitted before
    /// <see cref="ConfigureSerilog"/> runs (early host startup) still reach the console.
    /// </summary>
    /// <param name="logger">Existing <see cref="ILogger"/> instance (typically <see cref="Log.Logger"/>).</param>
    /// <returns>The same logger for chaining.</returns>
    public static ILogger ConfigureSerilogBootstrapLogger(this ILogger logger)
    {
        Log.Logger = new LoggerConfiguration()
            .AddDefaultLoggerConfiguration()
            .CreateBootstrapLogger();

        return logger;
    }

    private static void CleanupDevelopmentLogs(string serviceName)
    {
        var devLogBasePath = Path.Combine(Path.GetTempPath(), "stratara-logs");
        var currentLogFile = Path.Combine(devLogBasePath, $"{serviceName}.log");

        if (!File.Exists(currentLogFile))
        {
            return;
        }

        try { File.Delete(currentLogFile); }
        catch (IOException) { /* best effort — Serilog will recreate it on next write */ }
        catch (UnauthorizedAccessException) { /* best effort — Serilog will recreate it on next write */ }
    }

    private static LoggerConfiguration AddDefaultLoggerConfiguration(this LoggerConfiguration loggerConfiguration)
    {
        loggerConfiguration
            .Destructure.UsingAttributes()
            .Enrich.FromLogContext()
            .MinimumLevel.Warning()
            .WriteTo.Async(wt => wt
                .Console(
                    outputTemplate: "[{Level:u3}] {Message:lj}{NewLine}{Exception}",
                    theme: AnsiConsoleTheme.Code)
            );

        return loggerConfiguration;
    }
}
