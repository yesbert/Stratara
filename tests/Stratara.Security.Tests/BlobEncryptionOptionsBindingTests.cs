using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stratara.Security.Tests;

public class BlobEncryptionOptionsBindingTests
{
    [Fact]
    public void AddStrataraBlobEncryption_ReadsTheBlobEncryptionSection()
    {
        var builder = HostWithLegacyPurpose();

        builder.Services.AddStrataraBlobEncryption();

        Assert.True(Resolve(builder.Services).LegacyBlobsCarryPurpose);
    }

    [Fact]
    public void AddStrataraBlobEncryption_CodeAfterTheRegistration_Wins()
    {
        var builder = HostWithLegacyPurpose();

        builder.Services.AddStrataraBlobEncryption();
        builder.Services.Configure<StrataraBlobEncryptionOptions>(o => o.LegacyBlobsCarryPurpose = false);
        builder.Services.AddStrataraBlobEncryption();

        Assert.False(Resolve(builder.Services).LegacyBlobsCarryPurpose);
        Assert.Single(builder.Services, d => d.ImplementationType == typeof(StrataraBlobEncryptionOptionsBinding));
    }

    [Fact]
    public void AddStrataraBlobEncryption_WithoutConfiguration_ResolvesTheDefaults()
    {
        var services = new ServiceCollection();

        services.AddStrataraBlobEncryption();

        Assert.False(Resolve(services).LegacyBlobsCarryPurpose);
    }

    [Fact]
    public void AddStrataraFileKeyStore_TheConfigurationPassedIn_WinsOverTheContainers()
    {
        var builder = HostWithLegacyPurpose();
        var passed = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stratara:BlobEncryption:LegacyBlobsCarryPurpose"] = "false",
            })
            .Build();

        builder.Services.AddStrataraFileKeyStore(passed);

        Assert.False(Resolve(builder.Services).LegacyBlobsCarryPurpose);
    }

    private static HostApplicationBuilder HostWithLegacyPurpose()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stratara:BlobEncryption:LegacyBlobsCarryPurpose"] = "true",
        });
        return builder;
    }

    private static StrataraBlobEncryptionOptions Resolve(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IOptions<StrataraBlobEncryptionOptions>>().Value;
}
