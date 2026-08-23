using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Security;

namespace Stratara.Security;

/// <summary>
/// Development-only <see cref="IKeyStore"/> that returns a single deterministic key derived from a
/// fixed pass-phrase. Used as the fallback when no production key store (e.g.
/// <c>AddStrataraFileKeyStore</c>) has been registered.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never use in production, staging, QA, or any environment that handles real or
/// production-derived data.</strong> The constructor enforces a whitelist guard — it throws
/// <see cref="InvalidOperationException"/> in any environment whose name is not exactly
/// <c>Development</c>, so a host can never silently encrypt with the world-known test pass-phrase
/// baked into the shipping NuGet.
/// </para>
/// <para>
/// Register a real <see cref="IKeyStore"/> (the file-backed envelope store, an HSM, Key Vault, KMS,
/// etc.) before calling the security composition so the <c>TryAdd</c> default never resolves.
/// </para>
/// </remarks>
public sealed class DummyKeyStore : IKeyStore
{
    private const string FixedKeyId = "Development::dummy:v1";
    private const string DefaultKeyPhrase = "StrataraTestKey";

    private readonly byte[] _fixedKey;

    /// <summary>Creates a <see cref="DummyKeyStore"/> with the default test key-phrase.</summary>
    /// <param name="environment">The host environment — used to enforce the Development-only whitelist guard.</param>
    /// <exception cref="InvalidOperationException">Thrown when the host environment is anything other than <c>Development</c>.</exception>
    public DummyKeyStore(IHostEnvironment environment) : this(environment, DefaultKeyPhrase) { }

    /// <summary>Creates a <see cref="DummyKeyStore"/> with a custom key-phrase (test scenarios).</summary>
    /// <param name="environment">The host environment — used to enforce the Development-only whitelist guard.</param>
    /// <param name="keyPhrase">Pass-phrase to derive the deterministic key from.</param>
    /// <exception cref="InvalidOperationException">Thrown when the host environment is anything other than <c>Development</c>.</exception>
    public DummyKeyStore(IHostEnvironment environment, string keyPhrase)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"DummyKeyStore is restricted to the Development environment (current: '{environment.EnvironmentName}'). " +
                "Register a real IKeyStore (AddStrataraFileKeyStore, HSM, Azure Key Vault, AWS KMS, etc.) BEFORE the security " +
                "composition so the TryAdd fallback never resolves.");
        }

        _fixedKey = SHA256.HashData(Encoding.UTF8.GetBytes(keyPhrase));
    }

    /// <inheritdoc/>
    public ValueTask<KeyMaterial> GetOrCreateCurrentKeyAsync(KeyScope scope, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new KeyMaterial(FixedKeyId, _fixedKey));

    /// <inheritdoc/>
    public ValueTask<byte[]?> GetDataEncryptionKeyAsync(string keyId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<byte[]?>((byte[])_fixedKey.Clone());

    /// <inheritdoc/>
    public ValueTask<string> RotateAsync(KeyScope scope, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(FixedKeyId);

    /// <summary>
    /// Not supported. The development store derives its key from a fixed pass-phrase and holds no
    /// key material it could destroy, so a revocation here would shred nothing.
    /// </summary>
    /// <param name="keyId">The key to revoke.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    public ValueTask RevokeAsync(string keyId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(EraseNotSupported("RevokeAsync"));

    /// <summary>
    /// Not supported. The development store derives its key from a fixed pass-phrase and holds no
    /// key material it could destroy, so an erasure here would shred nothing.
    /// </summary>
    /// <param name="scope">The scope to erase.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    public ValueTask EraseScopeAsync(KeyScope scope, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(EraseNotSupported("EraseScopeAsync"));

    private static string EraseNotSupported(string operation) =>
        $"DummyKeyStore.{operation} is not supported. The development key store derives a single key from a fixed " +
        "pass-phrase and stores no key material, so it cannot destroy any: completing successfully would report an " +
        "erasure that never happened. Register a real IKeyStore (AddStrataraFileKeyStore, HSM, Azure Key Vault, " +
        "AWS KMS, etc.) to exercise crypto-shredding.";
}
