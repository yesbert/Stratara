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

    private static IHostApplicationBuilder NewBuilder() =>
        Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });
}
