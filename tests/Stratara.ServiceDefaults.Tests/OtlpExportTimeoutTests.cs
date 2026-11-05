using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;

namespace Stratara.ServiceDefaults.Tests;

/// <summary>
/// Reads and restores a process environment variable, so no other test may run beside these.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";
}

/// <summary>
/// With an OTLP endpoint configured, the export timeout defaults to 5000 ms unless the host configures one. The
/// exporter reads its options from the host's configuration, which has loaded the process environment before any
/// telemetry is configured — so the default has to reach that configuration to apply at all.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class OtlpExportTimeoutTests : IDisposable
{
    private const string TimeoutKey = "OTEL_EXPORTER_OTLP_TIMEOUT";

    private readonly string? _timeoutBefore = Environment.GetEnvironmentVariable(TimeoutKey);

    public OtlpExportTimeoutTests() => Environment.SetEnvironmentVariable(TimeoutKey, null);

    public void Dispose() => Environment.SetEnvironmentVariable(TimeoutKey, _timeoutBefore);

    [Fact]
    public void Without_a_configured_timeout_the_exporter_waits_five_seconds()
    {
        var builder = NewBuilderExportingOverOtlp();

        builder.ConfigureOpenTelemetry();

        Assert.Equal(5000, ExporterTimeout(builder));
    }

    [Fact]
    public void A_configured_timeout_is_kept()
    {
        var builder = NewBuilderExportingOverOtlp();
        builder.Configuration[TimeoutKey] = "1234";

        builder.ConfigureOpenTelemetry();

        Assert.Equal(1234, ExporterTimeout(builder));
    }

    [Fact]
    public void A_timeout_in_the_process_environment_is_kept()
    {
        Environment.SetEnvironmentVariable(TimeoutKey, "2345");
        var builder = NewBuilderExportingOverOtlp();

        builder.ConfigureOpenTelemetry();

        Assert.Equal(2345, ExporterTimeout(builder));
    }

    [Fact]
    public void Configuring_telemetry_leaves_the_process_environment_alone()
    {
        var builder = NewBuilderExportingOverOtlp();

        builder.ConfigureOpenTelemetry();

        Assert.Null(Environment.GetEnvironmentVariable(TimeoutKey));
    }

    private static int ExporterTimeout(IHostApplicationBuilder builder)
    {
        using var sp = builder.Services.BuildServiceProvider();
        return sp.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>().Get(Options.DefaultName).TimeoutMilliseconds;
    }

    /// <summary>A host whose configuration holds the process environment, as every default host builder's does.</summary>
    private static IHostApplicationBuilder NewBuilderExportingOverOtlp()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317";
        return builder;
    }
}
