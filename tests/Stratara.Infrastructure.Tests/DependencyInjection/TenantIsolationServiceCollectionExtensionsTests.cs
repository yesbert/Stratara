using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Mediator;
using Stratara.Mediator.Multitenancy;

namespace Stratara.Infrastructure.Tests.DependencyInjection;

public class TenantIsolationServiceCollectionExtensionsTests
{
    [Fact]
    public void AddStrataraTenantIsolation_CalledTwice_InstallsEachBehaviorOnce()
    {
        var services = new ServiceCollection();
        services.AddStrataraTenantIsolation();
        services.AddStrataraTenantIsolation();

        Assert.Single(services, d => d.ServiceType == typeof(IPipelineBehavior<,>));
        Assert.Single(services, d => d.ServiceType == typeof(IPipelineBehavior<>));
    }

    [Fact]
    public void AddStrataraTenantIsolation_CalledTwice_DoesNotResetTheConfiguredMode()
    {
        var services = new ServiceCollection();
        services.AddStrataraTenantIsolation(o => o.Mode = TenantIsolationMode.Strict);
        services.AddStrataraTenantIsolation();
        var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<TenantIsolationOptions>>();

        Assert.Equal(TenantIsolationMode.Strict, options.Value.Mode);
    }

    [Fact]
    public void TenantIsolationOptions_BindFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stratara:TenantIsolation:Mode"] = nameof(TenantIsolationMode.Strict)
            })
            .Build();

        var services = new ServiceCollection();
        services.AddStrataraTenantIsolation();
        services.Configure<TenantIsolationOptions>(configuration.GetSection("Stratara:TenantIsolation"));
        var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<TenantIsolationOptions>>();

        Assert.Equal(TenantIsolationMode.Strict, options.Value.Mode);
    }
}
