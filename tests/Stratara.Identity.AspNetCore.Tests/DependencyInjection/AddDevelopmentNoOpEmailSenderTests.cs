using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stratara.Identity.AspNetCore.Services;

namespace Stratara.Identity.AspNetCore.Tests.DependencyInjection;

public class AddDevelopmentNoOpEmailSenderTests
{
    private sealed class TestUser : IdentityUser;

    [Fact]
    public void AddDevelopmentNoOpEmailSender_InDevelopmentEnvironment_RegistersNoOpAsScoped()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });

        builder.AddDevelopmentNoOpEmailSender<TestUser>();

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IEmailSender<TestUser>));
        Assert.Equal(typeof(IdentityNoOpEmailSender<TestUser>), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddDevelopmentNoOpEmailSender_InProductionEnvironment_Throws()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
        });

        var ex = Assert.Throws<InvalidOperationException>(() => builder.AddDevelopmentNoOpEmailSender<TestUser>());
        Assert.Contains("IdentityNoOpEmailSender", ex.Message);
        Assert.Contains("production", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddDevelopmentNoOpEmailSender_InStagingEnvironment_DoesNotThrow()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Staging,
        });

        builder.AddDevelopmentNoOpEmailSender<TestUser>();

        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IEmailSender<TestUser>));
    }
}
