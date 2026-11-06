namespace Stratara.Abstractions.Security;

/// <summary>
/// Raised when data cannot be decrypted because the key it names no longer resolves: the key version was revoked, its
/// scope erased, or the key is unknown to the store. A caller may answer it as data that is gone. Any other failure of
/// a key store is not this exception and should not be answered that way.
/// </summary>
public sealed class KeyUnavailableException : InvalidOperationException
{
    /// <summary>Creates the exception for a key that does not resolve.</summary>
    /// <param name="keyId">The key identifier the data names.</param>
    public KeyUnavailableException(string keyId)
        : base($"Decryption key '{keyId}' is not available (revoked, erased, or unknown).")
    {
        KeyId = keyId;
    }

    /// <summary>The key identifier the data names. It is stored with the data, so it is not authenticated.</summary>
    public string KeyId { get; }
}
