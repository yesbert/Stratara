using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Stratara.ServiceDefaults.AspNetCore.Tests;

public class HealthCheckExtensionsTests
{
    [Fact]
    public void AddDefaultHealthChecks_RegistersSelfCheckTaggedLive()
    {
        var builder = NewBuilder();

        builder.AddDefaultHealthChecks();

        var sp = builder.Services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        var selfCheck = Assert.Single(options.Registrations, r => r.Name == "self");
        Assert.Contains("live", selfCheck.Tags);
    }

    [Fact]
    public void AddDefaultHealthChecks_RegistersHealthCheckService()
    {
        var builder = NewBuilder();

        builder.AddDefaultHealthChecks();

        Assert.Contains(builder.Services, d => d.ServiceType == typeof(HealthCheckService));
    }

    [Fact]
    public void AddDefaultHealthChecks_ReturnsSameBuilderForChaining()
    {
        var builder = NewBuilder();

        var returned = builder.AddDefaultHealthChecks();

        Assert.Same(builder, returned);
    }

    private static IHostApplicationBuilder NewBuilder() =>
        Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });
}
