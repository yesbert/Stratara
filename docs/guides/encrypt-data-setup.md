# Encrypt Sensitive Data

> **Derived page.** The behaviour described here is specified by the `data-encryption` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

Stratara provides AES-GCM encryption at serialization time via the `[EncryptData]` attribute. Tenant-aware **Additional Authenticated Data (AAD)** binds each ciphertext to the tenant — so a leaked key in tenant A's ciphertext can't be replayed against tenant B's record.

## Mark a property

```csharp
using Stratara.Abstractions.Security;

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
// appsettings: "Stratara:KeyStore": { "MasterKeyBase64": "<32 random bytes, base64>", "StorePath": "keystore.json" }
builder.Services.AddStrataraFileKeyStore(builder.Configuration);
```

`AddStrataraFileKeyStore` registers an `EnvelopeFileKeyStore` — it stores **KEK-wrapped, versioned per-`KeyScope` data-encryption keys** (the KEK comes from `IMasterKeyProvider`; the default `FileMasterKeyProvider` reads the base64 KEK from config). Generate the KEK with `openssl rand -base64 32` (it must decode to **exactly 32 bytes** — the KEK is used directly as an AES-256-GCM key) and supply it via a secret store, never source control. Prefer an HSM / Key Vault / KMS `IKeyStore` implementation for the KEK custody seam in regulated environments — register it the same way, before `AddSecurity()`.

## Keys, scopes, and blobs

A key is identified by a **`KeyScope`** — a `DataSensitivityLevel` (`None` / `UserScoped` / `TenantScoped` / `Confidential`) optionally narrowed to a tenant and/or user. The store derives a stable, versioned key id from the scope, so rotation keeps older ciphertext readable while `RevokeAsync` / `EraseScopeAsync` implement GDPR Art. 17 crypto-shredding.

For large payloads (attachments, exports), use `ISecureBlobEncryptor` directly — it binds the stream to a `KeyScope` **and** a `purpose` via the associated data:

<!-- stratara-snippet-ignore: narrative fragment - the encryptor and the stream come from the surrounding text -->
```csharp
await using var cipher = await blobEncryptor.EncryptAsync(
    plainStream,
    new KeyScope(DataSensitivityLevel.TenantScoped, tenantId.ToString()),
    purpose: "attachment",
    cancellationToken);
```

## Pick the level your data can actually satisfy

A level that names a dimension needs that dimension to exist. `TenantScoped` means *this value is
encrypted under a key belonging to this tenant* — which is only true if there is a tenant.

An aggregate without one is a normal thing to have: a customer, an organisation, anything above the
tenant in your hierarchy. Marking a field on it `TenantScoped` used to appear to work and quietly did
something else. The tenant on an event row is not nullable, so a tenant-less aggregate supplies the
empty identifier rather than nothing, and every such value across every subject collapsed into one
scope:

```
TenantScoped:00000000-0000-0000-0000-000000000000:00000000-0000-0000-0000-000000000000
```

One scope is one key. Erasing one subject would have erased all of them, so crypto-shredding was not
available at that level even though the annotation implied it. Nothing said so.

**Since 4.0.0 this is refused** outside development, with a message naming the level and what was
missing. In development it warns and proceeds, so local work is not blocked while the mistake is
still visible.

What is refused is the collapse to a *single system-wide key* — a level claiming isolation with
nothing at all to isolate by. A **coarser** scope than the level names is fine: a `UserScoped` value
carrying only a tenant resolves to a per-tenant key, which is weaker than the name suggests but still
separates tenants. The framework itself binds an event's payload to its stream's owner that way.

**Use `Confidential` for data that has no tenant.** It claims exactly what happens — one system-wide
key, no per-subject isolation — instead of implying isolation that is not there:

```csharp
[EncryptData(DataSensitivityLevel.Confidential)]
public sealed class OrganisationRegistered
{
    public string LegalName { get; set; } = "";
}
```

Two things to know:

- **Existing data stays readable.** The refusal governs writing only. Values already encrypted into
  the degenerate scope decrypt exactly as before — a guard on the read path would destroy access to
  them rather than protect anything.
- **`Confidential` still needs a tenant *value*, just not a meaningful one.** The additional
  authenticated data is built from the tenant whatever the level, so passing an explicit `null` is
  refused by a separate, older check. On the event path this never comes up: the value is the empty
  identifier, which is accepted.

If per-subject shredding above the tenant is what you actually need, that is a different question
from this one, and Stratara does not model a dimension above the tenant today.

## The AAD binding

When Stratara serializes an `[EncryptData]` property, it includes the current `TenantId` from `SessionContext` as the AAD:

```
ciphertext = AES-GCM-Encrypt(key, plaintext, nonce, AAD = TenantId)
```

Decryption requires the **same** AAD. If a ciphertext is moved between tenants, decryption fails with `CryptographicException` — defense-in-depth against cross-tenant data leakage.

## Reading blobs written before the v2 format

Blob ciphertext carries a leading version byte since v2. A stream written before that has no version
byte, and how to read it depends on whether the writer emitted a length-prefixed `purpose` field —
which the framework cannot tell from the bytes. Say which, through the `Stratara:BlobEncryption`
section (`StrataraBlobEncryptionOptions.SectionName`):

```jsonc
{
  "Stratara": {
    "BlobEncryption": {
      "LegacyBlobsCarryPurpose": false
    }
  }
}
```

The default is `false` — a legacy stream is read as carrying no purpose field, and `"blob"` is
assumed. New streams are always written in the v2 format regardless of this setting, so this is a
read-path compatibility switch and nothing else.

## EncryptionMetadataDriftGuard

At host-start, `EncryptionMetadataDriftGuard` (registered as `IHostedService` by `AddSecurity()`) walks the **Trusted-Type-Allowlist** and checks every type's `EncryptionMetadata.RequiresEncryption` against the actual `[EncryptData]` attributes. If they drift (someone removed `[EncryptData]` but didn't update the metadata), the host fails-fast.

This catches a class of bugs that's easy to introduce: marking a property `[EncryptData]` initially, then dropping the attribute in a refactor — without re-keying the historical events.

## Operational considerations

- **Rotation keeps old ciphertext readable.** `IKeyStore.RotateAsync(scope)` adds a new current key version; older versions stay resolvable, so existing events decrypt without a backfill. Use `RevokeAsync` / `EraseScopeAsync` when you *want* old ciphertext to become unreadable (crypto-shred).
- **Persisted ciphertext is opaque to projections.** Projections see the decrypted plaintext via `ISecureJsonSerializer`. Make sure your projection-worker has the key access.
- **The bus carries ciphertext.** Command payloads and event bundles are serialized through `ISecureJsonSerializer` on the way out, so `[EncryptData]` fields travel encrypted regardless of any other option — bus consumers without key access can't decrypt, by design. This is independent of `BusEnvelopeIntegrityOptions.Mode`, which decides whether envelopes are *signed*, not whether payloads are encrypted.
