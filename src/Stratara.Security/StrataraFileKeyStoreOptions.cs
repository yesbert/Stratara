namespace Stratara.Security;

/// <summary>
/// Options for the file-backed key store and master-key provider registered by
/// <c>AddStrataraFileKeyStore</c>.
/// </summary>
public sealed class StrataraFileKeyStoreOptions
{
    /// <summary>The configuration section these options bind from by default.</summary>
    public const string SectionName = "Stratara:KeyStore";

    /// <summary>
    /// Base64-encoded master key-encryption key (KEK). Must decode to at least 32 bytes (AES-256).
    /// Generate with <c>openssl rand -base64 48</c> and supply via a secret store, not source control.
    /// </summary>
    public string? MasterKeyBase64 { get; set; }

    /// <summary>
    /// Path to the JSON file that stores the KEK-wrapped data-encryption keys. Defaults to
    /// <c>keystore.json</c> in the current directory.
    /// </summary>
    public string StorePath { get; set; } = "keystore.json";
}
