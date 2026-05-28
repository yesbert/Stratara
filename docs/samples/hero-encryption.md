# Hero Sample — Encryption

**Concept**: `[EncryptData]` fields stay confidential across tenants by binding the AES-GCM authentication tag to the tenant id as AAD. Cross-tenant decryption fails by cryptography, not by query filtering. The full *why* lives in the [Tenant-Aware Encryption](../concepts/tenant-aware-encryption.md) concept page; this is the runnable proof.

- **Code**: [`samples/Stratara.Sample.Encryption`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.Encryption)
- **Lines**: ~120
- **Read time**: 5–10 min
- **Dependencies**: none — pure in-memory, no database, no DI container, no real key store.

## What you'll see

1. **`TenantAwareEncryptor`** — AES-256-GCM with one master key and AAD = tenant-id bytes.
2. **Two tenants seal the same SSN** — `"123-45-6789"` is encrypted under tenant A and tenant B. The console prints both ciphertexts (different — fresh nonces every seal).
3. **Same-tenant decryption** — each tenant reads its own row back, plaintext recovered.
4. **Cross-tenant attack** — taking tenant A's sealed value and trying to decrypt under tenant B raises `AuthenticationTagMismatchException` (a `CryptographicException` subclass) because the AAD doesn't match.
5. **Reverse direction** — same result. The AAD binding is symmetric.

## Running

```bash
dotnet run --project samples/Stratara.Sample.Encryption
```

Expected output (abridged):

```
=== Stratara Encryption ===

--- Seal the same SSN under two different tenants ---
  Plaintext (both):  '123-45-6789'
  Alice ciphertext:  95DE82D0… (tag AFE936C8…)
  Bob   ciphertext:  33BA0CDF… (tag ACB34420…)

--- Read each customer back under its own tenant context ---
  Alice: '123-45-6789'  (tenant A reads tenant A — OK)
  Bob:   '123-45-6789'  (tenant B reads tenant B — OK)

--- Cross-tenant attack: take Alice's row from the DB, try to decrypt as tenant B ---
  CAUGHT: AuthenticationTagMismatchException — The computed authentication tag did not match the input authentication tag.
  AES-GCM rejected the authentication tag because the AAD (tenant id) does not match.
  This holds even with the correct master key. The tenant binding is mathematical.
```

## How this maps to the real Stratara

| Sample | Real Stratara |
|---|---|
| `[Encrypted]` marker (sample-local) | `[EncryptData]` from `Stratara.Abstractions` |
| `TenantAwareEncryptor` (hardcoded master key) | `IFieldEncryptor` backed by `IKeyStore` — `AzureKeyVaultKeyStore`, `AwsKmsKeyStore`, HSM |
| Manual `Encrypt` / `Decrypt` calls | EF Core value converter — transparent on save/load |
| Tenant id passed explicitly | `ISessionContextProvider.Current.TenantId` — ambient per request |
| One master key, AAD-only separation | Per-tenant HKDF-derived data-encryption keys **on top of** the AAD binding |

## See also

- **[Encrypt Sensitive Data](../guides/encrypt-data-setup.md)** — the wire-it-up-in-your-own-host guide (how to register `IKeyStore`, key rotation considerations, the `EncryptionMetadataDriftGuard` startup check).

## Sister hero sample

- **[Hero Sample — TamperProof](hero-tamper-proof.md)** — the integrity counterpart. Detects post-commit row mutation at the next verifier pass.
