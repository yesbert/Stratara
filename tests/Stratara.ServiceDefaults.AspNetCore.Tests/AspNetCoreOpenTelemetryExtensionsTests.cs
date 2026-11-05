using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Stratara.ServiceDefaults.AspNetCore.Tests;

public class AspNetCoreOpenTelemetryExtensionsTests
{
    [Fact]
    public void ConfigureAspNetOpenTelemetry_RegistersTracerAndMeterProviders()
    {
        var builder = NewBuilder();

        builder.ConfigureAspNetOpenTelemetry();

        var sp = builder.Services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<TracerProvider>());
        Assert.NotNull(sp.GetService<MeterProvider>());
    }

    [Fact]
    public void ConfigureAspNetOpenTelemetry_ReturnsSameBuilderForChaining()
    {
        var builder = NewBuilder();

        var returned = builder.ConfigureAspNetOpenTelemetry();

        Assert.Same(builder, returned);
    }

    /// <summary>
    /// The quick start calls both methods. With an OTLP endpoint configured, the exporter must be registered once:
    /// OpenTelemetry refuses a second <c>UseOtlpExporter</c> on the same service collection when the providers are
    /// built, and the host does not start.
    /// </summary>
    [Fact]
    public void Both_methods_with_an_otlp_endpoint_build_the_providers()
    {
        var builder = NewBuilderExportingOverOtlp();

        builder.ConfigureOpenTelemetry();
        builder.ConfigureAspNetOpenTelemetry();

        using var sp = builder.Services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<TracerProvider>());
        Assert.NotNull(sp.GetService<MeterProvider>());
    }

    [Fact]
    public void Both_methods_in_the_other_order_and_twice_build_the_providers()
    {
        var builder = NewBuilderExportingOverOtlp();

        builder.ConfigureAspNetOpenTelemetry();
        builder.ConfigureOpenTelemetry();
        builder.ConfigureAspNetOpenTelemetry();
        builder.ConfigureOpenTelemetry();

        using var sp = builder.Services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<TracerProvider>());
        Assert.NotNull(sp.GetService<MeterProvider>());
    }

    [Fact]
    public void Request_instrumentation_is_added_once_when_the_base_pipeline_was_configured_first()
    {
        var builder = NewBuilder();

        builder.ConfigureOpenTelemetry();
        builder.ConfigureAspNetOpenTelemetry();

        using var sp = builder.Services.BuildServiceProvider();
        Assert.Equal(1, InstrumentationCount(sp.GetRequiredService<TracerProvider>(), "AspNetCore"));
    }

    [Fact]
    public void Request_instrumentation_is_added_once_when_it_is_configured_twice()
    {
        var builder = NewBuilder();

        builder.ConfigureAspNetOpenTelemetry();
        builder.ConfigureAspNetOpenTelemetry();

        using var sp = builder.Services.BuildServiceProvider();
        Assert.Equal(1, InstrumentationCount(sp.GetRequiredService<TracerProvider>(), "AspNetCore"));
        Assert.Equal(1, InstrumentationCount(sp.GetRequiredService<TracerProvider>(), "HttpClient"));
    }

    private static int InstrumentationCount(TracerProvider provider, string name)
    {
        var instrumentations = provider.GetType()
            .GetProperty("Instrumentations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(provider) as System.Collections.IEnumerable;
        Assert.NotNull(instrumentations);
        return instrumentations.Cast<object>().Count(i => i.GetType().Name.Contains(name, StringComparison.Ordinal));
    }

    private static IHostApplicationBuilder NewBuilderExportingOverOtlp()
    {
        var builder = NewBuilder();
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317";
        return builder;
    }

    private static IHostApplicationBuilder NewBuilder() =>
        Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });
}
