namespace Stratara.Security;

/// <summary>Options for the AES-GCM blob encryptor registered by the Stratara.Security DI extensions.</summary>
public sealed class StrataraBlobEncryptionOptions
{
    /// <summary>The configuration section these options bind from by default.</summary>
    public const string SectionName = "Stratara:BlobEncryption";

    /// <summary>
    /// How to interpret a legacy stream that lacks the v2 leading version byte. When
    /// <see langword="true"/>, the legacy stream is expected to carry a length-prefixed
    /// <c>purpose</c> field after the key id (VeloxRAG heritage); when <see langword="false"/>,
    /// no purpose field is present and <c>"blob"</c> is assumed (NextPA heritage). New streams are
    /// always written in the v2 format regardless of this setting. Default: <see langword="false"/>.
    /// </summary>
    public bool LegacyBlobsCarryPurpose { get; set; }
}
