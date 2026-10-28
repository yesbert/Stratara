using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Stratara.Abstractions.Security;

namespace Stratara.Security;

/// <summary>On-disk model for <see cref="EnvelopeFileKeyStore"/> — only KEK-wrapped DEKs, never plaintext keys.</summary>
[ExcludeFromCodeCoverage]
internal sealed class KeyStoreFile
{
    /// <summary>The newest file format this release reads and the one every write stamps; 2 added <see cref="ErasedSubjects"/>.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public Dictionary<string, ScopeEntry> Scopes { get; set; } = new();
    public List<ErasedSubjectEntry> ErasedSubjects { get; set; } = [];
}

/// <summary>A subject marked erased: the store creates no key for a scope it covers.</summary>
[ExcludeFromCodeCoverage]
internal sealed class ErasedSubjectEntry
{
    [JsonConverter(typeof(JsonStringEnumConverter<KeySubjectKind>))]
    public KeySubjectKind Kind { get; set; }
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset ErasedAt { get; set; }

    public KeySubject ToSubject() => Kind == KeySubjectKind.Tenant ? KeySubject.Tenant(Id) : KeySubject.User(Id);
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
