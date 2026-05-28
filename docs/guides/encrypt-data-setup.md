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
// IKeyStore, IEncryptionFactory, ISecureJsonSerializer.
```

You **must** register an `IKeyStore` implementation in non-Development environments. The default `DummyKeyStore` (registered automatically) is gated to Development only — production hosts that don't override it will fail-fast at startup via the `KeyStoreStartupProbe`.

```csharp
services.AddSingleton<IKeyStore, MyAzureKeyVaultKeyStore>();
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

- **Don't rotate keys mid-stream.** Stratara doesn't currently rotate per-event keys; an `IKeyStore` change requires a backfill.
- **Persisted ciphertext is opaque to projections.** Projections see the decrypted plaintext via `ISecureJsonSerializer`. Make sure your projection-worker has the key access.
- **The bus carries ciphertext** when `BusEnvelopeIntegrityOptions.Mode != Off`. Bus consumers without key access can't decrypt — by design.
