using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stratara.Abstractions.Security;
using Xunit;

namespace Stratara.Security.Tests;

public class SecurityRegistrationTests
{
    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    private static IConfiguration Config()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stratara:KeyStore:MasterKeyBase64"] = TestSupport.NewKekBase64(),
                ["Stratara:KeyStore:StorePath"] = Path.Combine(Directory.CreateTempSubdirectory("stratara-reg").FullName, "keystore.json"),
            })
            .Build();

    [Fact]
    public void AddStrataraFileKeyStore_RegistersKeyStoreEncryptorAndProvider()
    {
        var services = Services();
        services.AddStrataraFileKeyStore(Config());
        var sp = services.BuildServiceProvider();

        Assert.IsType<EnvelopeFileKeyStore>(sp.GetRequiredService<IKeyStore>());
        Assert.IsType<FileMasterKeyProvider>(sp.GetRequiredService<IMasterKeyProvider>());
        Assert.NotNull(sp.GetRequiredService<ISecureBlobEncryptor>());
        Assert.NotNull(sp.GetRequiredService<IEncryptionFactory>());
        Assert.Contains(sp.GetServices<IHostedService>(), s => s is FileKeyStoreStartupProbe);
    }

    [Fact]
    public void FileKeyStore_WinsOverDummyFallback_WhenRegisteredFirst()
    {
        var services = Services();
        services.AddStrataraFileKeyStore(Config());

        // Simulate a later AddSecurity() composition adding the development fallback.
        services.TryAddSingleton<IKeyStore, DummyKeyStore>();

        var sp = services.BuildServiceProvider();
        Assert.IsType<EnvelopeFileKeyStore>(sp.GetRequiredService<IKeyStore>());
    }
}
