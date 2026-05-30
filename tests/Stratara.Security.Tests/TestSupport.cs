using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Stratara.Security.Tests;

/// <summary>Shared helpers for the Stratara.Security test suite.</summary>
internal static class TestSupport
{
    public static string NewKekBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static StrataraFileKeyStoreOptions NewOptions(string kekBase64)
    {
        var dir = Directory.CreateTempSubdirectory("stratara-keystore-test");
        return new StrataraFileKeyStoreOptions
        {
            MasterKeyBase64 = kekBase64,
            StorePath = Path.Combine(dir.FullName, "keystore.json"),
        };
    }

    public static EnvelopeFileKeyStore NewKeyStore(StrataraFileKeyStoreOptions options)
    {
        var wrapped = Options.Create(options);
        var provider = new FileMasterKeyProvider(wrapped);
        return new EnvelopeFileKeyStore(provider, wrapped, NullLogger<EnvelopeFileKeyStore>.Instance);
    }
}
