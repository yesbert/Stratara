namespace Stratara.Security;

/// <summary>Options for the AES-GCM blob encryptor registered by the Stratara.Security DI extensions.</summary>
public sealed class StrataraBlobEncryptionOptions
{
    /// <summary>The configuration section these options bind from by default.</summary>
    public const string SectionName = "Stratara:BlobEncryption";

    /// <summary>
    /// How to interpret a legacy stream that lacks the v2 leading version byte. When
    /// <see langword="true"/>, the legacy stream is expected to carry a length-prefixed
    /// <c>purpose</c> field after the key id; when <see langword="false"/>, no purpose field is
    /// present and <c>"blob"</c> is assumed. New streams are always written in the v2 format
    /// regardless of this setting. Default: <see langword="false"/>.
    /// </summary>
    public bool LegacyBlobsCarryPurpose { get; set; }

    /// <summary>
    /// Whether new streams are written in the v2 format instead of v3. A v2 stream binds its
    /// ciphertext to the tenant and purpose only; a v3 stream binds it to the whole scope — level,
    /// tenant, user — and the purpose. Both formats are read regardless of this setting.
    /// </summary>
    /// <remarks>
    /// Turn this on for a rolling upgrade while hosts older than the release that introduced v3
    /// still read the blobs, because they cannot parse a v3 stream, and turn it off again once every
    /// host is upgraded. Default: <see langword="false"/>.
    /// </remarks>
    public bool WriteV2Blobs { get; set; }
}
