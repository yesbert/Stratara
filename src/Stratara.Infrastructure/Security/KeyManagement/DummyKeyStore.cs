using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Stratara.Abstractions.Security;

namespace Stratara.Infrastructure.Security.KeyManagement;

/// <summary>
/// Development-only <see cref="IKeyStore"/> implementation that returns a single deterministic key derived
/// from a fixed pass-phrase.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never use in production, staging, QA, or any environment that handles real or
/// production-derived data.</strong> Since 3.0.11 the constructor enforces a whitelist guard — it
/// throws <see cref="InvalidOperationException"/> in any environment whose name is not exactly
/// <c>Development</c>. Round-3-Audit Finding KI-05: before 3.0.11 the guard only blocked
/// <see cref="HostEnvironmentEnvExtensions.IsProduction"/>, which let staging / QA / preview hosts
/// silently encrypt with the world-known <c>"StrataraTestKey"</c> pass-phrase baked into the
/// shipping NuGet.
/// </para>
/// <para>
/// Register a real key-store implementation (HSM, Azure Key Vault, AWS KMS, etc.) before calling
/// <c>AddSecurity()</c> so the <c>TryAddSingleton</c> default never resolves. The
/// <c>KeyStoreStartupProbe</c> logs a warning at host start when <see cref="DummyKeyStore"/> is the
/// resolved <see cref="IKeyStore"/> implementation, even in <c>Development</c>, so an unintentional
/// dependency on the dummy is loud rather than silent.
/// </para>
/// </remarks>
internal sealed class DummyKeyStore : IKeyStore
{
    private const string FixedKeyId = "dummy-test-key-id";
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
                "Register a real IKeyStore implementation (HSM, Azure Key Vault, AWS KMS, etc.) BEFORE calling AddSecurity() " +
                "so the TryAddSingleton fallback never resolves. Example: " +
                "services.AddSingleton<IKeyStore, AzureKeyVaultKeyStore>(); services.AddSecurity();");
        }

        _fixedKey = DeriveKey(keyPhrase);
    }

    /// <inheritdoc/>
    public ValueTask<byte[]?> GetDataEncryptionKeyAsync(string keyId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<byte[]?>(_fixedKey);

    /// <inheritdoc/>
    public ValueTask RevokeAsync(string keyId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask<string> EnsureKeyAsync(DataSensitivityLevel level, Guid? tenantId, Guid? userId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(FixedKeyId);

    private static byte[] DeriveKey(string input) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(input));
}
