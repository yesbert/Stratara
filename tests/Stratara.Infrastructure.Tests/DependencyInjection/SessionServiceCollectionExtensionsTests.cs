using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Session;
using Stratara.Sessions.Session;

namespace Stratara.Infrastructure.Tests.DependencyInjection;

public class SessionServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSessionContext_ReadsTheSessionContextSection()
    {
        var builder = HostWithTenantHeaderAllowed();

        builder.Services.AddSessionContext();

        Assert.True(Resolve(builder.Services).AllowTenantHeader);
    }

    [Fact]
    public void AddSessionContext_CodeAfterTheRegistration_Wins()
    {
        var builder = HostWithTenantHeaderAllowed();

        builder.Services.AddSessionContext();
        builder.Services.Configure<SessionContextOptions>(o => o.AllowTenantHeader = false);

        Assert.False(Resolve(builder.Services).AllowTenantHeader);
    }

    [Fact]
    public void AddSessionContext_CalledAgainAfterCode_DoesNotReapplyTheSection()
    {
        var builder = HostWithTenantHeaderAllowed();

        builder.Services.AddSessionContext();
        builder.Services.Configure<SessionContextOptions>(o => o.AllowTenantHeader = false);
        builder.Services.AddSessionContext();

        Assert.False(Resolve(builder.Services).AllowTenantHeader);
        Assert.Single(builder.Services, d => d.ImplementationType == typeof(SessionContextOptionsBinding));
    }

    [Fact]
    public void AddSessionContext_WithoutConfiguration_ResolvesTheDefaults()
    {
        var services = new ServiceCollection();

        services.AddSessionContext();

        Assert.False(Resolve(services).AllowTenantHeader);
    }

    private static HostApplicationBuilder HostWithTenantHeaderAllowed()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SessionContext:AllowTenantHeader"] = "true",
        });
        return builder;
    }

    private static SessionContextOptions Resolve(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IOptions<SessionContextOptions>>().Value;
}
