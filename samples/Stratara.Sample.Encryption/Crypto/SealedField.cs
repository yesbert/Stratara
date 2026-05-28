namespace Stratara.Sample.Encryption.Crypto;

// What gets persisted in place of an [Encrypted] field. Nonce + tag are stored
// alongside the ciphertext; the AAD (tenant id) is NOT stored — it's reconstructed
// from the row's tenant context at decryption time. That coupling is the security
// property: the ciphertext only opens if the row is being read under the same
// tenant identity that sealed it.
public sealed record SealedField(byte[] Nonce, byte[] Ciphertext, byte[] Tag);
