---
title: "Encrypt Data Setup"
description: "Turning on AES-GCM field encryption with [EncryptData], choosing a key scope, and why a scope that cannot isolate is refused outside development."
---

# Encrypt Sensitive Data

> **Derived page.** The behaviour described here is specified by the `data-encryption` and
> `outbox-and-messaging` capabilities under `openspec/specs/`. Those specifications are the source;
> this page explains and illustrates them. Where the two disagree, the specification is right and this
> page is a bug.

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

`AddStrataraFileKeyStore` registers an `EnvelopeFileKeyStore` — it stores **KEK-wrapped, versioned per-`KeyScope` data-encryption keys** (the KEK comes from `IMasterKeyProvider`; the default `FileMasterKeyProvider` reads the base64 KEK from config). Generate the KEK with `openssl rand -base64 32` (it must decode to **exactly 32 bytes** — the KEK is used directly as an AES-256-GCM key) and supply it via a secret store, never source control. Prefer an HSM / Key Vault / KMS `IKeyStore` implementation for the KEK custody seam in regulated environments — register it the same way, before `AddSecurity()`. Implement `ListScopesAsync` on it too: an erasure asks the key store for every key naming the subject, and without the listing it can shred only the keys the directory names, which miss a key shared with a former member or with an operator acting in the tenant from outside it. Implement `MarkSubjectErasedAsync` as well: an erasure marks the subject before shredding, and from then on `GetOrCreateCurrentKeyAsync` and `RotateAsync` must throw `KeyScopeErasedException` for every scope `KeySubject.Covers`. Without it, work still running after an erasure is given a fresh key for the erased subject.

### Several processes, one key store

Several processes can share one store file, for example containers that bind-mount the same host
directory at `StorePath`. The file store is built for that:

- **A key one process creates, another resolves.** A process that is already running and asked for a
  key it has not seen reloads the file once and finds it. Nobody has to restart.
- **Two processes creating a key for the same new scope at once end up with the same key.** Creation
  serialises through a lock file beside the store (`<StorePath>.lock`) and re-reads the file before it
  writes, so the second writer reuses the first writer's key instead of silently replacing it.
- **Two processes creating keys for different scopes at once keep both.**
- **A subject one process marks erased, every process refuses.** The mark is written to the file
  under the same lock, and a process re-reads the file before it creates a key.

**Upgrade every process that shares the file together.** 4.6.0 writes the file in format version 2,
which adds the erased-subject marks. A process on an earlier release reads a version-2 file without
complaint but does not know the marks: it creates keys for an erased subject and drops the marks the
next time it writes the file. From 4.6.0 on, a store refuses to open a file in a format newer than it
knows, naming both versions, and leaves the file untouched. A version-1 file needs no migration; it is
written back as version 2 on the next change.

Every process must use the same master key (`Stratara:KeyStore:MasterKeyBase64`), because the keys in
the file are wrapped under it. A networked file system (NFS, SMB) is not supported: it guarantees
neither an atomic rename nor a reliable lock file.

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

**A command dispatched from a session without a tenant is not data without a tenant.** An anonymous
endpoint, or a saga reacting to a fact one recorded, dispatches under a session that names no
tenant, and a `TenantScoped` field on such a command is refused for the reason above. `Confidential`
is the wrong answer when the data does belong to a tenant: it would never be erased with it. Let the
command name its owner instead — see
[Whose key a command's protected fields are under](write-a-command-handler.md#whose-key-a-commands-protected-fields-are-under).

## The AAD binding

When Stratara serializes an `[EncryptData]` property, it includes the current `TenantId` from `SessionContext` as the AAD:

```
ciphertext = AES-GCM-Encrypt(key, plaintext, nonce, AAD = TenantId)
```

Decryption requires the **same** AAD. If a ciphertext is moved between tenants, decryption fails with `CryptographicException` — defense-in-depth against cross-tenant data leakage.

The tenant is the session's only where nothing more specific states the owner. An event is bound to
its **stream's** owner, whoever appends it, and a command that implements `INamesDataOwner` is bound
to the owner it names, whoever dispatches it. A recorded field change is an event like any other, and
its value is additionally bound to the property it names — see
[Changes to a marked property](#changes-to-a-marked-property).

## Blob formats, and reading older ones

A blob's ciphertext is bound to the whole `KeyScope` it was written for — level, tenant and user —
and to its purpose, so a blob does not decrypt under another user of the same tenant, under the
tenant-level scope, or under another level. This is the v3 format, which starts with the byte `0x03`.
Blobs written by earlier releases stay readable: a v2 blob (leading `0x02`) was bound to the tenant
and the purpose only, and still decrypts exactly as before.

A reader that knows which purpose it wrote with should say so, through the
`DecryptAsync(stream, scope, purpose, cancellationToken)` overload: a blob written for another
purpose then fails authentication instead of decrypting. A legacy blob that records no purpose
cannot be checked against one and decrypts as before.

### Rolling upgrades

A host on a release before v3 cannot read a v3 blob. While such hosts still read blobs this host
writes, keep writing v2 and switch once every host is upgraded:

```jsonc
{
  "Stratara": {
    "BlobEncryption": {
      "WriteV2Blobs": true
    }
  }
}
```

The default is `false`. Both formats are read whatever the setting.

### Blobs written before the v2 format

A stream written before v2 has no version byte, and how to read it depends on whether the writer
emitted a length-prefixed `purpose` field — which the framework cannot tell from the bytes. Say which,
through the same `Stratara:BlobEncryption` section (`StrataraBlobEncryptionOptions.SectionName`):

```jsonc
{
  "Stratara": {
    "BlobEncryption": {
      "LegacyBlobsCarryPurpose": false
    }
  }
}
```

`AddSecurity()`, `AddStrataraBlobEncryption()` and `AddStrataraFileKeyStore(configuration)` read the section
from the host's configuration. The default is `false` — a legacy stream is read as carrying no purpose field, and `"blob"` is
assumed. It only affects how a legacy stream is read, never the format new streams are written in.

## Revoking a version, and reading what it encrypted

### Revoking a version destroys exactly that version

`IKeyStore.RevokeAsync(keyId)` makes that one key version permanently unresolvable. From then on
`GetDataEncryptionKeyAsync` returns `null` for it, and data encrypted under it cannot be recovered.
Other versions of the same scope are untouched.

The scope stays usable. The next write that asks for the scope's current key
(`GetOrCreateCurrentKeyAsync`) gets the highest version that is left, or a new one when none is, so
new writes keep succeeding. The file store and `InMemoryKeyStore` from `Stratara.Testing` behave the
same way. A scope does not go
dead because its latest version was revoked. To destroy *every* version of a scope, which is the
GDPR Art. 17 crypto-shred of a subject, use `EraseScopeAsync(scope)`. Revoking every version is not
an erasure and leaves no mark; only `MarkSubjectErasedAsync` keeps a subject from being given a key
again.

### Unreadable encrypted fields degrade rather than fail

Erasing a subject's key must not make every record that mentions the subject unreadable. When
`ISecureJsonSerializer` deserializes an object whose `[EncryptData]` field was encrypted under a key
that no longer exists — revoked, its scope erased, or its subject erased — **that field reads as
absent** and the object's other fields are recovered normally. An event carrying one shredded field
still rehydrates, and a projection still sees everything else in it. A replay that meets an erased
subject's events delivers every one of them, in order, with those fields absent, and runs to the end.

Two consequences to design for:

- **Give an encrypted field a type that can be absent**, such as a `string`, another reference type,
  or a nullable value type like `decimal?`. Such a field comes back as `null`. A non-nullable value
  type such as `decimal` comes back as its default, `0`, which a reader cannot tell apart from a
  stored zero.
- **`[EncryptData]` on a class encrypts the object as one unit.** If that key is gone, the whole
  object reads as `null`, not just one field. That is why an event or aggregate type may not be
  marked as a whole (below): nothing of it would be left to deliver, and the replay would stop.

**Data written before a type was marked for encryption stays readable.** If you add `[EncryptData]`
to a type that already has ordinary serialized data, that data is read as ordinary data rather than
rejected. A field that is stored in the clear is taken as it is, while a field stored encrypted is
decrypted. New writes are encrypted from the moment the attribute is in place.

## Changes to a marked property

An update applied as a change set records one `FieldChangedEvent<TAggregate>` per changed property,
and you can append one yourself. The event carries no marks of its own: **it is protected as the
property it names.** Where that property is marked on the aggregate, the recorded value is stored and
published as ciphertext, at the property's own level, for the stream's owner — the key the property's
value in the aggregate's snapshot is under, so an erasure that shreds one shreds the other. A change
to an unmarked property is stored as ordinary JSON and asks for no key.

```json
{"PropertyName":"Email","NewValue":{"__enc":true,"v":1,"alg":1,"kid":"…","n":"…","t":"…","ct":"…"}}
```

- **The name stays readable.** Replay needs it before any key is fetched. It is also bound to the
  value: a stored change whose property name was altered fails with `CryptographicException` rather than
  assigning one property's value to another. An envelope copied between a snapshot and a recorded
  change fails the same way.
- **Replay sets the value, then its absence.** While the key exists, rebuilding sets the property to
  the recorded value. Once the key is destroyed the event reads as carrying no value, the property is
  set to `null` or to the default of a non-nullable value type, and the replay goes on with the next
  event.
- **Removing a mark does not strand anything.** Whether a value is decrypted depends on what is
  stored, not on the current mark, so ciphertext keeps reading after the mark is gone.

### Rolling upgrades of recorded changes

A host on a version that did not protect recorded changes cannot read one that is protected. **Upgrade
every host that reads the stream or consumes its events first, and the hosts that write to it after
them.** There is no setting that keeps writing the old form: it would be an off-switch for encryption
of data you marked as needing it.

What an earlier host does when it meets a protected change depends on the property's type:

- For a `string`, a number, a `Guid`, a date, an enum or a collection, applying the event throws
  `JsonException`, because the envelope cannot be converted to the property's type. The replay or the
  event handler fails, and delivery retries.
- For a property typed `object` or `JsonElement`, the envelope itself is assigned as the value. For a
  record or a class, or a dictionary that can hold the envelope's members, the envelope reads as an
  object with none of the expected values. **There is no failure in these cases, only a wrong value.**
  The reader-first order is what prevents this.

The same applies to rolling back. A host rolled back to an earlier version fails on the protected
changes written in the meantime, so roll back every host together or not at all.

### Field changes recorded in the clear

Changes recorded before this protection existed, or before the property was marked, hold the value in
plain JSON. They keep replaying as they are, and the framework does not rewrite them: a stream's chain
hashes each entry's payload, so a rewrite would destroy the evidence the chain exists to give.

**This is existing exposure that an erasure does not reach.** Destroying a key does nothing to a value
that was never encrypted under it, in the store or in any copy of it.

The affected events are the field changes that name a marked property and whose value is not an
envelope. With the default PostgreSQL naming, this lists them for one aggregate and only reads; put in
your aggregate's full name, its assembly and its marked properties:

```sql
SELECT stream_id, version, tenant_id, user_id
FROM event_stream_entry
WHERE event_type_name LIKE 'Stratara.Shared.EventSourcing.FieldChangedEvent`1[[MyApp.Customer, MyApp,%'
  AND data_json::jsonb ->> 'PropertyName' IN ('Email', 'Phone')
  AND jsonb_typeof(data_json::jsonb -> 'NewValue') <> 'null'
  AND data_json::jsonb -> 'NewValue' -> '__enc' IS NULL;
```

You then have two options, and neither is free:

- **Accept and record the exposure.** Nothing changes in the store; your erasure records state that
  these values are outside what a key's destruction reaches.
- **Replace the payloads, as a deliberate operator act outside the framework.** Re-encrypt or null
  those payloads, then re-chain and re-anchor the stream from the first altered entry. Every entry
  from that point gets a new hash, and the chain no longer proves that those entries are unchanged.
  Whoever relies on that evidence has to be told. The framework ships no migration for this, on
  purpose: it would automate breaking the evidence.

## Data that belongs to a person

Some data is a person's, not a tenant's: a profile with their name, e-mail and language that follows
them into every tenant they belong to. Keyed to one of those tenants, it would be shredded when that
tenant is erased. Record it for an owner that names the user alone:

<!-- stratara-snippet-ignore: narrative fragment - the event source, the stream id and the person come from the surrounding text -->
```csharp
public sealed record PersonProfileCreated(
    Guid PersonId,
    [property: EncryptData] string? DisplayName,
    [property: EncryptData] string? Email,
    string Language);

await events.CreateOnBehalfOfAsync<PersonProfile>(
    profileId, new PersonProfileCreated(personId, "Ada", "ada@example.com", "en"), EventSubject.ForUser(personId));
await events.SaveChangesAsync();
```

The stream records the person and no tenant — the reserved default tenant, `Guid.Empty` — and keeps
that owner for every later event, whatever tenant the session that appends to it names. Its
encrypted fields, at whatever level they are marked, are keyed to the person alone: the person's
erasure shreds them, and no tenant's erasure reaches them. It reads normally from a session of any of the person's tenants.

- **Only a stated owner creates such a stream.** A session whose tenant failed closed to the reserved
  default still gives an event no owner, so a request without a tenant never writes data that
  belongs to nobody in particular.
- **The scope is fixed when the data is written.** The ciphertext is bound to the owner it was
  written for, so data already written under one of the person's tenants stays that tenant's and is
  shredded with it. Moving it means writing it anew.
- **What a tenant may keep about a former member is the tenant's data**, and is keyed to the tenant
  as before.

## EncryptionMetadataDriftGuard

At host-start, `EncryptionMetadataDriftGuard` (registered as `IHostedService` by `AddSecurity()`) walks the **Trusted-Type-Allowlist** and checks every type's `EncryptionMetadata.RequiresEncryption` against the actual `[EncryptData]` attributes — for a `FieldChangedEvent<TAggregate>`, those of the aggregate's properties. If they drift (someone removed `[EncryptData]` but didn't update the metadata), the host fails-fast.

This catches a class of bugs that's easy to introduce: marking a property `[EncryptData]` initially, then dropping the attribute in a refactor — without re-keying the historical events.

**What a stream stores is encrypted field by field.** The same guard refuses to start a host in which a registered aggregate, or an event type a registered aggregate applies, carries `[EncryptData]` on the type itself. Such a payload has no field left once its subject is erased: the event cannot be delivered and the snapshot cannot be restored, so a replay or a rebuild stops at the erasure — and keeps stopping there. An event type no aggregate applies is refused when it is appended, before anything is written. Mark the properties that hold personal data instead. Payloads already written as a whole are still read — the serializer recognises the envelope by its shape — but after their subject's erasure they still stop a replay, and an upcaster cannot help, since it sees ciphertext only. Commands and other types may still be marked as a whole.

## Operational considerations

- **Rotation keeps old ciphertext readable.** `IKeyStore.RotateAsync(scope)` adds a new current key version; older versions stay resolvable, so existing events decrypt without a backfill. Use `RevokeAsync` / `EraseScopeAsync` when you *want* old ciphertext to become unreadable (crypto-shred).
- **Persisted ciphertext is opaque to projections.** Projections see the decrypted plaintext via `ISecureJsonSerializer`. Make sure your projection-worker has the key access.
- **The bus carries ciphertext.** Command payloads and event bundles are serialized through `ISecureJsonSerializer` on the way out, so `[EncryptData]` fields travel encrypted regardless of any other option — bus consumers without key access can't decrypt, by design. This is independent of `BusEnvelopeIntegrityOptions.Mode`, which decides whether envelopes are *signed*, not whether payloads are encrypted.
