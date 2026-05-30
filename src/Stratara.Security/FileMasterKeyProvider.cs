using Microsoft.Extensions.Options;
using Stratara.Abstractions.Security;

namespace Stratara.Security;

/// <summary>
/// <see cref="IMasterKeyProvider"/> that reads the master KEK from
/// <see cref="StrataraFileKeyStoreOptions.MasterKeyBase64"/> (typically sourced from a secret
/// store or read-only mounted <c>secrets.json</c>). Validates the key is present and at least
/// 32 bytes (AES-256) at construction so a misconfigured host fails fast.
/// </summary>
internal sealed class FileMasterKeyProvider : IMasterKeyProvider
{
    private const int MinimumKeyBytes = CryptoConstants.KeySize;

    private readonly ReadOnlyMemory<byte> _masterKey;

    /// <summary>Initialise the provider, decoding and validating the configured KEK.</summary>
    /// <param name="options">The bound key-store options carrying the base64 KEK.</param>
    /// <exception cref="InvalidOperationException">The KEK is missing, not valid base64, or shorter than 32 bytes.</exception>
    public FileMasterKeyProvider(IOptions<StrataraFileKeyStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Value.MasterKeyBase64;

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"No master key configured. Set '{StrataraFileKeyStoreOptions.SectionName}:{nameof(StrataraFileKeyStoreOptions.MasterKeyBase64)}' " +
                "to a base64-encoded key of at least 32 bytes. Generate one with: openssl rand -base64 48");
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
                "is not valid base64. Generate one with: openssl rand -base64 48", ex);
        }

        if (decoded.Length < MinimumKeyBytes)
        {
            throw new InvalidOperationException(
                $"The configured master key is {decoded.Length} bytes; at least {MinimumKeyBytes} are required (AES-256). " +
                "Generate one with: openssl rand -base64 48");
        }

        _masterKey = decoded;
    }

    /// <inheritdoc/>
    public ValueTask<ReadOnlyMemory<byte>> GetMasterKeyAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_masterKey);
}
