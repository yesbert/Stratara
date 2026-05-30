namespace Stratara.Abstractions.Security;

/// <summary>
/// Supplies the master key-encryption key (KEK) used to wrap and unwrap the data-encryption keys
/// (DEKs) held by an <see cref="IKeyStore"/>. This is the custody seam: the default file-backed
/// provider can later be swapped for an HSM / KMS / vault provider without changing
/// <see cref="IKeyStore"/> or the stored (wrapped) DEK data.
/// </summary>
public interface IMasterKeyProvider
{
    /// <summary>Return the master KEK bytes used to wrap/unwrap DEKs.</summary>
    /// <param name="cancellationToken">Propagated to the underlying provider.</param>
    /// <returns>The KEK bytes (at least 32 bytes for AES-256).</returns>
    ValueTask<ReadOnlyMemory<byte>> GetMasterKeyAsync(CancellationToken cancellationToken = default);
}
