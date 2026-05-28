using System.Diagnostics.CodeAnalysis;

namespace Stratara.Infrastructure.Security.Serialization;

[ExcludeFromCodeCoverage]
internal static class SecurityConstants
{
    public const string EncryptionMarker = "__enc";
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const string ClassScope = "class";
}