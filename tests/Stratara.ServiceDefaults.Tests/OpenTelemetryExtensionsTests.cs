using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Stratara.ServiceDefaults.Tests;

public class OpenTelemetryExtensionsTests
{
    [Fact]
    public void ConfigureOpenTelemetry_RegistersTracerAndMeterProviders()
    {
        var builder = NewBuilder();

        builder.ConfigureOpenTelemetry();

        var sp = builder.Services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<TracerProvider>());
        Assert.NotNull(sp.GetService<MeterProvider>());
    }

    [Fact]
    public void ConfigureOpenTelemetry_ReturnsSameBuilderForChaining()
    {
        var builder = NewBuilder();

        var returned = builder.ConfigureOpenTelemetry();

        Assert.Same(builder, returned);
    }

    [Fact]
    public void ConfigureOpenTelemetry_OptionalMetricsCallback_IsInvoked()
    {
        var builder = NewBuilder();
        var metricsCalled = false;

        builder.ConfigureOpenTelemetry(configureMetrics: _ => metricsCalled = true);

        Assert.True(metricsCalled);
    }

    [Fact]
    public void ConfigureOpenTelemetry_OptionalTracingCallback_IsInvoked()
    {
        var builder = NewBuilder();
        var tracingCalled = false;

        builder.ConfigureOpenTelemetry(configureTracing: _ => tracingCalled = true);

        Assert.True(tracingCalled);
    }

    [Fact]
    public void ConfigureOpenTelemetry_WithoutOtlpEndpoint_DoesNotRegisterOtlpExporter()
    {
        var builder = NewBuilder();

        builder.ConfigureOpenTelemetry();

        Assert.DoesNotContain(builder.Services, d =>
            d.ImplementationType?.FullName?.Contains("Otlp", StringComparison.Ordinal) == true);
    }

    private static IHostApplicationBuilder NewBuilder() =>
        Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });
}
