using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stratara.Abstractions.Messaging;

namespace Stratara.Infrastructure.Tests.DependencyInjection;

public class IntegrityServiceCollectionExtensionsTests
{
    [Fact]
    public void AddBusEnvelopeIntegrity_WithBase64Key_RegistersSignerAndBindsOptions()
    {
        var sharedKey = new byte[32];
        Array.Fill(sharedKey, (byte)0x42);
        var services = new ServiceCollection();

        services.AddBusEnvelopeIntegrity(Convert.ToBase64String(sharedKey), BusEnvelopeIntegrityMode.Permissive);

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IBusEnvelopeSigner>());
        var options = sp.GetRequiredService<IOptions<BusEnvelopeIntegrityOptions>>().Value;
        Assert.Equal(BusEnvelopeIntegrityMode.Permissive, options.Mode);
        Assert.Equal(sharedKey, options.SharedKey);
    }

    [Fact]
    public void AddBusEnvelopeIntegrity_WithBase64Key_DefaultsToStrict()
    {
        var sharedKey = new byte[32];
        var services = new ServiceCollection();

        services.AddBusEnvelopeIntegrity(Convert.ToBase64String(sharedKey));

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<BusEnvelopeIntegrityOptions>>().Value;
        Assert.Equal(BusEnvelopeIntegrityMode.Strict, options.Mode);
    }

    /// <summary>
    /// Both checks happen at registration rather than at the first signed message: a mistyped secret
    /// should fail where the host can still fix it, not on a message in flight.
    /// </summary>
    [Fact]
    public void AddBusEnvelopeIntegrity_WithMalformedBase64_ThrowsAtRegistration()
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddBusEnvelopeIntegrity("not base64!!"));

        Assert.Contains("base64", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddBusEnvelopeIntegrity_WithTooShortKey_ThrowsAtRegistration()
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddBusEnvelopeIntegrity(Convert.ToBase64String(new byte[16])));

        Assert.Contains("16 bytes", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("32", thrown.Message, StringComparison.Ordinal);
    }

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
