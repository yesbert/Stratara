using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Stratara.Infrastructure.Security.Serialization;

[ExcludeFromCodeCoverage]
internal sealed class EncryptedWrapper
{
    [JsonPropertyName("__enc")] public bool Encrypted { get; set; }

    [JsonPropertyName("v")] public int Version { get; set; } = 1;

    [JsonPropertyName("alg")] public EncryptionAlgorithm Algorithm { get; set; } = EncryptionAlgorithm.Aes256Gcm;

    [JsonPropertyName("kid")] public string KeyId { get; set; } = string.Empty;

    [JsonPropertyName("n")] public byte[] Nonce { get; set; } = [];

    [JsonPropertyName("t")] public byte[] Tag { get; set; } = [];

    [JsonPropertyName("ct")] public byte[] CipherText { get; set; } = [];
}