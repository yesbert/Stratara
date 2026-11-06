using System.Diagnostics.CodeAnalysis;

namespace Stratara.Infrastructure.Security.Serialization;

[ExcludeFromCodeCoverage]
internal static class SecurityConstants
{
    public const string EncryptionMarker = "__enc";
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const string ClassScope = "class";

    // Part of the stored form: changing it makes every protected recorded change unreadable. It keeps
    // this scope apart from the bare property name a field of the aggregate itself is encrypted under.
    public const string RecordedChangeScopePrefix = "field-changed:";
}