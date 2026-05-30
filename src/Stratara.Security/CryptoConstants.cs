namespace Stratara.Security;

/// <summary>AES-GCM parameters shared by the Stratara.Security crypto primitives.</summary>
internal static class CryptoConstants
{
    /// <summary>AES-GCM nonce size in bytes (96 bits).</summary>
    public const int NonceSize = 12;

    /// <summary>AES-GCM authentication-tag size in bytes (128 bits).</summary>
    public const int TagSize = 16;

    /// <summary>Data-encryption-key size in bytes (AES-256).</summary>
    public const int KeySize = 32;
}
