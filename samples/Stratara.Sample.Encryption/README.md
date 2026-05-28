# Stratara.Sample.Encryption

**One of two Why-Stratara hero samples.** Shows how `[EncryptData]` fields stay confidential across tenants even when the database leaks — distilled to ~120 lines of in-memory code.

## The pitch

Multi-tenant databases usually defend with **query filtering**: every query gets a `WHERE TenantId = @currentTenant` predicate. That's fine for *normal* paths, but it's a soft fence. A buggy migration, a misconfigured backup restore, a developer running ad-hoc SQL through the wrong session — and one tenant's row becomes readable from another tenant's context.

Stratara's `[EncryptData]` adds a **hard fence**: AES-GCM with the authentication tag bound to the tenant id as associated data (AAD). If a row sealed under tenant A is decrypted under tenant B, AES-GCM rejects the auth tag at the cryptographic layer. **Even with the correct master key.** Same plaintext, different tenants, two unrelated ciphertexts — and no path between them.

## What to look at, in order

1. **`Crypto/EncryptedAttribute.cs`** — the marker attribute. In real Stratara it's `[EncryptData]` and is read by an EF Core value converter; here it's just a marker so the *intent* on `Customer.SocialSecurityNumber` is visible.

2. **`Crypto/SealedField.cs`** — a tiny record holding `(Nonce, Ciphertext, Tag)`. This is what hits the database in place of the plaintext. The AAD (tenant id) is **not** stored — it's reconstructed from the row's tenant context at decryption time. That coupling *is* the security property.

3. **`Crypto/TenantAwareEncryptor.cs`** — AES-256-GCM. `Encrypt(plaintext, tenantId)` produces a `SealedField`; `Decrypt(sealedField, tenantId)` throws `CryptographicException` if the AAD doesn't match what the field was sealed under.

4. **`Program.cs`** — the demo script:
   - Seal the same SSN `"123-45-6789"` under two tenants. Print both ciphertexts.
   - Decrypt each under its own tenant context — both succeed.
   - Cross-tenant attack: take tenant A's sealed value, try to decrypt under tenant B — `CryptographicException`.
   - Same attack, opposite direction — same exception.

## Run it

```bash
dotnet run --project samples/Stratara.Sample.Encryption
```

Expected: same-tenant decryption succeeds, both cross-tenant attempts raise `CryptographicException` with explicit explanation that the AAD mismatch caused it.

## How this maps to the real Stratara

| Sample | Real Stratara |
|---|---|
| `[Encrypted]` marker | `[EncryptData]` from `Stratara.Abstractions` |
| `TenantAwareEncryptor` (master key hardcoded) | `IFieldEncryptor` backed by `IKeyStore` (`AzureKeyVaultKeyStore`, `AwsKmsKeyStore`, …) |
| Manual `Encrypt` / `Decrypt` call | EF Core value converter — transparent, fires automatically on save/load |
| Tenant id passed explicitly | `ISessionContextProvider.Current.TenantId` — ambient per request |
| One master key, AAD-only separation | Per-tenant key derivation via HKDF **on top of** AAD binding (belt and suspenders) |

## Concept doc

For the wider *why* — threat model, key-rotation story, why GCM specifically — see [Tenant-Aware Encryption](https://docs.stratara.tech/concepts/tenant-aware-encryption.html).

## Sister hero sample

- **[`Stratara.Sample.TamperProof`](../Stratara.Sample.TamperProof)** — the other side of the integrity story: detection. Hash-chained event streams catch any direct-DB row modification at the next verification pass.
