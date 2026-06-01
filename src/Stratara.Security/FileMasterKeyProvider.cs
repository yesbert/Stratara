using Microsoft.Extensions.Options;
using Stratara.Abstractions.Security;

namespace Stratara.Security;

/// <summary>
/// <see cref="IMasterKeyProvider"/> that reads the master KEK from
/// <see cref="StrataraFileKeyStoreOptions.MasterKeyBase64"/> (typically sourced from a secret
/// store or read-only mounted <c>secrets.json</c>). Validates the key is present and decodes to
/// exactly 32 bytes (AES-256) at construction so a misconfigured host fails fast — the KEK is fed
/// directly to <see cref="System.Security.Cryptography.AesGcm"/>, which only accepts AES key
/// sizes, so any other length would otherwise crash at the first key creation rather than at boot.
/// </summary>
internal sealed class FileMasterKeyProvider : IMasterKeyProvider
{
    private const int RequiredKeyBytes = CryptoConstants.KeySize;

    private readonly ReadOnlyMemory<byte> _masterKey;

    /// <summary>Initialise the provider, decoding and validating the configured KEK.</summary>
    /// <param name="options">The bound key-store options carrying the base64 KEK.</param>
    /// <exception cref="InvalidOperationException">The KEK is missing, not valid base64, or does not decode to exactly 32 bytes.</exception>
    public FileMasterKeyProvider(IOptions<StrataraFileKeyStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Value.MasterKeyBase64;

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"No master key configured. Set '{StrataraFileKeyStoreOptions.SectionName}:{nameof(StrataraFileKeyStoreOptions.MasterKeyBase64)}' " +
                "to a base64-encoded key of exactly 32 bytes (AES-256). Generate one with: openssl rand -base64 32");
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(configured);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"The configured master key ('{StrataraFileKeyStoreOptions.SectionName}:{nameof(StrataraFileKeyStoreOptions.MasterKeyBase64)}') " +
                "is not valid base64. Generate one with: openssl rand -base64 32", ex);
        }

        if (decoded.Length != RequiredKeyBytes)
        {
            throw new InvalidOperationException(
                $"The configured master key decodes to {decoded.Length} bytes; exactly {RequiredKeyBytes} are required (AES-256). " +
                "The KEK is used directly as an AES-GCM key, which accepts only 16/24/32-byte keys, so a non-32-byte KEK " +
                "would crash at the first key creation rather than here at boot. Generate a correct one with: openssl rand -base64 32");
        }

        _masterKey = decoded;
    }

    /// <inheritdoc/>
    public ValueTask<ReadOnlyMemory<byte>> GetMasterKeyAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_masterKey);
}
