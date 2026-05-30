# Stratara.Security

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Dependency-light key store and envelope encryption for Stratara. Provides a production
`IKeyStore` with KEK-wrapped, versioned per-scope data-encryption keys (rotation, revoke, and
crypto-shred), a file-backed master-key provider, and an AES-GCM blob encryptor — referencing only
`Stratara.Abstractions` + BCL crypto. **No** EF Core, RabbitMQ, Redis, or cloud SDKs in the graph.

## Quick start

```csharp
// appsettings / secrets:
// "Stratara": { "KeyStore": { "MasterKeyBase64": "<openssl rand -base64 48>", "StorePath": "/var/run/secrets/keystore.json" } }

builder.Services.AddStrataraFileKeyStore(builder.Configuration);

// Encrypt a blob bound to a tenant scope + purpose:
var scope = new KeyScope(DataSensitivityLevel.TenantScoped, tenantId: "acme-corp");
await using var encrypted = await encryptor.EncryptAsync(plainStream, scope, purpose: "attachment");
await using var plain = await encryptor.DecryptAsync(encrypted, scope);
```

## What's inside

- **`EnvelopeFileKeyStore`** (`IKeyStore`) — random 32-byte DEK per scope/version, **KEK-wrapped**
  with AES-256-GCM (wrap AAD bound to the key id, so a wrapped DEK can't be moved to another scope).
  The store file holds only wrapped DEKs + metadata, never plaintext. `RotateAsync` adds a version;
  `RevokeAsync` makes one version undecryptable; `EraseScopeAsync` deletes all versions for a scope
  (GDPR Art. 17 crypto-shred). DEKs are zeroed after use; the store file is written `0600` on Unix.
- **`FileMasterKeyProvider`** (`IMasterKeyProvider`) — KEK from `MasterKeyBase64`, validated ≥32
  bytes at startup. The custody seam: swap for an HSM / KMS / vault provider later without touching
  the stored data.
- **`AesGcmSecureBlobEncryptor`** (`ISecureBlobEncryptor`) — AES-GCM stream encryption with a
  `purpose`-bound AAD (`{tenant}||{purpose}`) and a versioned, self-describing format (v2 leading
  byte). Reads legacy streams without the version byte; set
  `Stratara:BlobEncryption:LegacyBlobsCarryPurpose` to match the legacy layout.
- **`DummyKeyStore`** — Development-only deterministic fallback (throws outside `Development`).

## Key id schema

`{level}:{tenant}:{user}:v{N}` — e.g. `TenantScoped:acme-corp::v1`. `GetOrCreateCurrentKeyAsync`
returns the highest non-revoked version (creating `v1` if none); `RotateAsync` creates `v{N+1}`.

## Dependencies

- `Stratara.Abstractions`
- `Stratara.Diagnostics`
- `Microsoft.Extensions.{Configuration,DependencyInjection,Hosting,Logging}.Abstractions`
- `Microsoft.Extensions.Options` (+ `Options.ConfigurationExtensions`)
