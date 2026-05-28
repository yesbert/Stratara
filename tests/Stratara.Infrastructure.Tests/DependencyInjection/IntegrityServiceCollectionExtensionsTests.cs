using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Messaging;

namespace Stratara.Infrastructure.Tests.DependencyInjection;

public class IntegrityServiceCollectionExtensionsTests
{
    [Fact]
    public void AddBusEnvelopeIntegrity_WithAction_RegistersSignerAndBindsOptions()
    {
        var sharedKey = new byte[32];
        Array.Fill(sharedKey, (byte)0x42);
        var services = new ServiceCollection();

        services.AddBusEnvelopeIntegrity(opt =>
        {
            opt.Mode = BusEnvelopeIntegrityMode.Strict;
            opt.SharedKey = sharedKey;
        });

        var sp = services.BuildServiceProvider();
        var signer = sp.GetRequiredService<IBusEnvelopeSigner>();
        var options = sp.GetRequiredService<IOptions<BusEnvelopeIntegrityOptions>>().Value;

        Assert.NotNull(signer);
        Assert.Equal(BusEnvelopeIntegrityMode.Strict, options.Mode);
        Assert.Equal(sharedKey, options.SharedKey);
    }

    [Fact]
    public void AddBusEnvelopeIntegrity_WithConfiguration_BindsModeFromSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BusEnvelopeIntegrity:Mode"] = "Permissive",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddBusEnvelopeIntegrity(configuration);

        Assert.Single(services, d =>
            d.ServiceType == typeof(IBusEnvelopeSigner) && d.Lifetime == ServiceLifetime.Singleton);
        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<BusEnvelopeIntegrityOptions>>().Value;
        Assert.Equal(BusEnvelopeIntegrityMode.Permissive, options.Mode);
    }

    [Fact]
    public void AddBusEnvelopeIntegrity_WithAction_ReturnsSameCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddBusEnvelopeIntegrity(_ => { });

        Assert.Same(services, result);
    }

    [Fact]
    public void AddBusEnvelopeIntegrity_WithConfiguration_ReturnsSameCollection()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var result = services.AddBusEnvelopeIntegrity(configuration);

        Assert.Same(services, result);
    }

    [Fact]
    public void AddBusEnvelopeIntegrity_NullActionConfigure_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddBusEnvelopeIntegrity((Action<BusEnvelopeIntegrityOptions>)null!));
    }

    [Fact]
    public void AddBusEnvelopeIntegrity_NullConfiguration_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() =>
            services.AddBusEnvelopeIntegrity((IConfiguration)null!));
    }
}
