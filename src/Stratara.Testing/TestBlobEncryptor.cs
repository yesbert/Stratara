using Microsoft.Extensions.DependencyInjection;
using Stratara.Abstractions.Security;

namespace Stratara.Testing;

/// <summary>
/// Factory for the framework's real AES-GCM <see cref="ISecureBlobEncryptor"/> backed by an
/// <see cref="InMemoryKeyStore"/>, so blob round-trips in tests exercise the production encryptor
/// (scope- and purpose-bound associated data, v2 stream format) without a master KEK or an
/// on-disk key file.
/// </summary>
public static class TestBlobEncryptor
{
    /// <summary>
    /// Build an AES-GCM <see cref="ISecureBlobEncryptor"/> over a fresh <see cref="InMemoryKeyStore"/>.
    /// </summary>
    /// <returns>A ready-to-use encryptor whose ciphertext decrypts only under the same scope + purpose.</returns>
    public static ISecureBlobEncryptor CreateAesGcm() => CreateAesGcm(new InMemoryKeyStore());

    /// <summary>
    /// Build an AES-GCM <see cref="ISecureBlobEncryptor"/> over a caller-supplied <see cref="IKeyStore"/>.
    /// </summary>
    /// <param name="keyStore">The key store the encryptor resolves DEKs from (e.g. a shared <see cref="InMemoryKeyStore"/>).</param>
    /// <returns>A ready-to-use encryptor bound to <paramref name="keyStore"/>.</returns>
    public static ISecureBlobEncryptor CreateAesGcm(IKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(keyStore);

        var services = new ServiceCollection();
        services.AddSingleton(keyStore);
        services.AddStrataraBlobEncryption();
        return services.BuildServiceProvider().GetRequiredService<ISecureBlobEncryptor>();
    }
}
