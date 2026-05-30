# Encrypt Sensitive Data

Stratara provides AES-GCM encryption at serialization time via the `[EncryptData]` attribute. Tenant-aware **Additional Authenticated Data (AAD)** binds each ciphertext to the tenant — so a leaked key in tenant A's ciphertext can't be replayed against tenant B's record.

## Mark a property

```csharp
using Stratara.Abstractions.Security.Encryption;

public sealed record CustomerCreated(
    Guid CustomerId,
    string Name,
    [property: EncryptData] string SocialSecurityNumber,
    [property: EncryptData] string BankAccountNumber);
```

The encryption happens at the **serialization boundary** — when Stratara writes the event to the store or the bus. In-memory the property is still the plaintext.

## Wire the infrastructure

```csharp
builder.AddCommonFrameworkServices();
// AddCommonFrameworkServices() transitively calls AddSecurity(), which wires
// ISecureJsonSerializer, the AES-GCM blob encryptor, and a Development-only
// DummyKeyStore fallback.
```

You **must** register a real `IKeyStore` in non-Development environments. The default `DummyKeyStore` (registered automatically via `TryAdd`) is gated to Development only — production hosts that don't override it fail-fast at startup via the `KeyStoreStartupProbe`.

The built-in production store ships in the dependency-light **`Stratara.Security`** package. Register it **before** `AddSecurity()` so it wins the `TryAdd` race:

```csharp
// appsettings: "Stratara:KeyStore": { "MasterKeyBase64": "<48 random bytes, base64>", "StorePath": "keystore.json" }
builder.Services.AddStrataraFileKeyStore(builder.Configuration);
```

`AddStrataraFileKeyStore` registers an `EnvelopeFileKeyStore` — it stores **KEK-wrapped, versioned per-`KeyScope` data-encryption keys** (the KEK comes from `IMasterKeyProvider`; the default `FileMasterKeyProvider` reads the base64 KEK from config). Generate the KEK with `openssl rand -base64 48` and supply it via a secret store, never source control. Prefer an HSM / Key Vault / KMS `IKeyStore` implementation for the KEK custody seam in regulated environments — register it the same way, before `AddSecurity()`.

## Keys, scopes, and blobs

A key is identified by a **`KeyScope`** — a `DataSensitivityLevel` (`None` / `UserScoped` / `TenantScoped` / `Confidential`) optionally narrowed to a tenant and/or user. The store derives a stable, versioned key id from the scope, so rotation keeps older ciphertext readable while `RevokeAsync` / `EraseScopeAsync` implement GDPR Art. 17 crypto-shredding.

For large payloads (attachments, exports), use `ISecureBlobEncryptor` directly — it binds the stream to a `KeyScope` **and** a `purpose` via the associated data:

```csharp
await using var cipher = await blobEncryptor.EncryptAsync(
    plainStream,
    new KeyScope(DataSensitivityLevel.TenantScoped, tenantId.ToString()),
    purpose: "attachment",
    cancellationToken);
```

## The AAD binding

When Stratara serializes an `[EncryptData]` property, it includes the current `TenantId` from `SessionContext` as the AAD:

```
ciphertext = AES-GCM-Encrypt(key, plaintext, nonce, AAD = TenantId)
```

Decryption requires the **same** AAD. If a ciphertext is moved between tenants, decryption fails with `CryptographicException` — defense-in-depth against cross-tenant data leakage.

## EncryptionMetadataDriftGuard

At host-start, `EncryptionMetadataDriftGuard` (registered as `IHostedService` by `AddSecurity()`) walks the **Trusted-Type-Allowlist** and checks every type's `EncryptionMetadata.RequiresEncryption` against the actual `[EncryptData]` attributes. If they drift (someone removed `[EncryptData]` but didn't update the metadata), the host fails-fast.

This catches a class of bugs that's easy to introduce: marking a property `[EncryptData]` initially, then dropping the attribute in a refactor — without re-keying the historical events.

## Operational considerations

- **Rotation keeps old ciphertext readable.** `IKeyStore.RotateAsync(scope)` adds a new current key version; older versions stay resolvable, so existing events decrypt without a backfill. Use `RevokeAsync` / `EraseScopeAsync` when you *want* old ciphertext to become unreadable (crypto-shred).
- **Persisted ciphertext is opaque to projections.** Projections see the decrypted plaintext via `ISecureJsonSerializer`. Make sure your projection-worker has the key access.
- **The bus carries ciphertext** when `BusEnvelopeIntegrityOptions.Mode != Off`. Bus consumers without key access can't decrypt — by design.
