using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Stratara.Identity.AspNetCore.Services;
using Stratara.Identity.Core.Abstractions;

namespace Stratara.Identity.AspNetCore.Tests.DependencyInjection;

public class AddAspNetIdentityWithSignInManagerTests
{
    private sealed class TestUser : IdentityUser;

    private sealed class TestIdentityDbContext(DbContextOptions<TestIdentityDbContext> options)
        : IdentityDbContext<TestUser>(options);

    [Fact]
    public void AddAspNetIdentityWithSignInManager_RegistersStrataraSignInManagerAsScoped()
    {
        var builder = NewBuilder();

        builder.AddAspNetIdentityWithSignInManager<TestUser, TestIdentityDbContext>();

        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(IStrataraSignInManager));
        Assert.Equal(typeof(AspNetSignInManager<TestUser>), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddAspNetIdentityWithSignInManager_RegistersLocalizationSoIdentityResourcesAreResolvable()
    {
        var builder = NewBuilder();

        builder.AddAspNetIdentityWithSignInManager<TestUser, TestIdentityDbContext>();

        Assert.Contains(builder.Services, d => d.ServiceType == typeof(IStringLocalizerFactory));
    }

    [Fact]
    public void AddAspNetIdentityWithSignInManager_DoesNotRegisterStrataraAuthenticationStateProvider()
    {
        var builder = NewBuilder();

        builder.AddAspNetIdentityWithSignInManager<TestUser, TestIdentityDbContext>();

        Assert.DoesNotContain(
            builder.Services,
            d => d.ServiceType == typeof(IStrataraAuthenticationStateProvider));
    }

    private static IHostApplicationBuilder NewBuilder()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddLogging();
        builder.Services.AddDbContextFactory<TestIdentityDbContext>(opt =>
            opt.UseInMemoryDatabase($"identity-test-{Guid.NewGuid():N}"));
        return builder;
    }
}
