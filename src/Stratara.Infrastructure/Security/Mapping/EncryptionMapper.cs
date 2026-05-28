using Stratara.Infrastructure.Security.Serialization;
using Stratara.Abstractions.Security;

namespace Stratara.Infrastructure.Security.Mapping;

internal static class EncryptionMapper
{
    public static EncryptedWrapper MapToEncryptedWrapper(this EncryptedData encryptedData, string keyId) =>
        new()
        {
            Encrypted = true,
            Version = 1,
            Algorithm = EncryptionAlgorithm.Aes256Gcm,
            KeyId = keyId,
            Nonce = encryptedData.Nonce,
            Tag = encryptedData.Tag,
            CipherText = encryptedData.CipherText
        };

    public static EncryptedData MapToEncryptedData(this EncryptedWrapper wrapper) => new()
    {
        CipherText = wrapper.CipherText,
        Nonce = wrapper.Nonce,
        Tag = wrapper.Tag
    };
}