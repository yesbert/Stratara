using System.Diagnostics.CodeAnalysis;

namespace Stratara.Security;

/// <summary>On-disk model for <see cref="EnvelopeFileKeyStore"/> — only KEK-wrapped DEKs, never plaintext keys.</summary>
[ExcludeFromCodeCoverage]
internal sealed class KeyStoreFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, ScopeEntry> Scopes { get; set; } = new();
}

/// <summary>All key versions for one <see cref="Stratara.Abstractions.Security.KeyScope"/>.</summary>
[ExcludeFromCodeCoverage]
internal sealed class ScopeEntry
{
    public string CurrentKeyId { get; set; } = string.Empty;
    public Dictionary<string, WrappedKeyEntry> Keys { get; set; } = new();
}

/// <summary>A single KEK-wrapped data-encryption key plus its metadata.</summary>
[ExcludeFromCodeCoverage]
internal sealed class WrappedKeyEntry
{
    public string WrappedKeyBase64 { get; set; } = string.Empty;
    public string WrapNonceBase64 { get; set; } = string.Empty;
    public string WrapTagBase64 { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public bool Revoked { get; set; }
}
