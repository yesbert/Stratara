using System.Diagnostics.CodeAnalysis;

namespace Stratara.Abstractions.Security;

/// <summary>
/// Ciphertext envelope produced by
/// <see cref="Stratara.Abstractions.Security.IEncryptionFactory"/>. Carries the
/// three components needed for AES-GCM decryption — none of them are secrets, but the
/// envelope is bound to the original associated-data via the authentication tag.
/// </summary>
[ExcludeFromCodeCoverage]
public readonly struct EncryptedData
{
    /// <summary>The encrypted payload.</summary>
    public required byte[] CipherText { get; init; }

    /// <summary>The nonce used during encryption — must be unique per (key, message).</summary>
    public required byte[] Nonce { get; init; }

    /// <summary>The 16-byte AES-GCM authentication tag — verified on decryption.</summary>
    public required byte[] Tag { get; init; }
}
