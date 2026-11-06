# Design

## Context

See proposal.md — Why. The current state, with the evidence each statement rests on:

- `ChangeSetHandler.ApplyChangeSetAsync` appends one `FieldChangedEvent<TAggregate>(PropertyName,
  NewValue)` per `ChangeDetail` through `IEventSource.AppendAsync`, then saves. Evidence: the
  implementation, `src/Stratara.Infrastructure/EventSourcing/ChangeSetHandler.cs`.
- `EventSource.AppendEventToStreamAsync` serializes every payload through
  `ISecureJsonSerializer.SerializeAsync(@event, subject.TenantId, subject.UserId)`, where `subject`
  is the stream's resolved owner. The stored `DataJson` is what the tamper-evident chain hashes and
  what the bus carries. Evidence: the implementation, `EventSource.cs`; the `event-sourcing-store`
  requirement "Events are persisted with their payload protected".
- `SecureJsonSerializer` decides per **type**: `MetadataCache.GetOrCreateMetadata(obj.GetType())`
  collects `[EncryptData]` from the class, its public properties and its primary-constructor
  parameters. `FieldChangedEvent<TAggregate>` has none, so `RequiresEncryption` is false and the
  whole event goes through `JsonSerializer.Serialize`. Evidence: the implementation.
- Per-property encryption wraps the value in an envelope `{"__enc":true,"v","alg","kid","n","t","ct"}`
  under the key `KeyScope(level, tenant, user)` and AAD `tenantId|userId|scope`, where `scope` is the
  serialized type's property name. The read path decrypts any property that *is* an envelope and
  passes every other value through — which is why data written before a type was marked stays
  readable. An unrecoverable key yields an absent value (null, or the type's default for a
  non-nullable value type). Evidence: the implementation; `SecureJsonSerializerTests`.
- On read, `EventMapperFactory` runs the upcaster pipeline on the stored `DataJson` string, then
  deserializes through the same serializer under `entry.TenantId` / `entry.UserId`. `NewValue` is
  `object?`, so it materializes as a `JsonElement`; `FieldChangedEventExtensions.ApplyPropertyChanged`
  → `PropertyAccessorCache.SetValueByName` converts a `JsonElement` to the property's type and assigns
  through a reflected setter. Evidence: the implementation; `FieldChangedEventExtensionsTests`.
- The aggregate's snapshot is serialized through the same serializer as the aggregate type, so a
  marked aggregate property is ciphertext there, under the same owner. Evidence: `SnapshotService`,
  `AggregationService`.
- `EncryptionMetadataDriftGuard` asserts, for each trusted type, that `RequiresEncryption` agrees
  with the presence of an `[EncryptData]` attribute on the type itself. Evidence: the implementation;
  `EncryptionMetadataDriftGuardTests`.

## Goals / Non-Goals

**Goals:**
- A recorded change to a marked property is ciphertext in the store and on the bus, under exactly
  the key the property's value in the snapshot uses, and unrecoverable after that key is destroyed.
- No change at all for unmarked properties, for events of any other type, and for the published API.
- Every field change already stored replays as before.

**Non-Goals:**
- Rewriting stored events. See Decision 4.
- Protecting the property *name*. It is structural, and replay needs it before any key is fetched.
- Changing how change sets are logged; the `observability` requirement already forbids values of
  marked fields in log messages, and `LogChangeSetCreated` is out of scope here.
- Fixing the upcaster-rename limitation of ordinary per-property encryption (an upcaster that renames
  a protected property of any event type already moves the ciphertext under a name its AAD does not
  carry). This change does not make it worse and does not address it.

## Decisions

### 1. Protection follows the property the event names, decided in the serializer

The serializer recognises `FieldChangedEvent<TAggregate>` (by generic type definition) and, per
instance, looks up `PropertyName` in the encryption metadata of `TAggregate` — the same
`MetadataCache` entry that protects the snapshot, so property marks and primary-constructor parameter
marks are honoured identically. If the named property is marked, `NewValue` is serialized with the
**aggregate property's declared type** (as the snapshot serializes it) and written as an envelope at
that attribute's `Level`; otherwise the event is written exactly as today.

- *Why in the serializer, not in `ChangeSetHandler`:* `FieldChangedEvent<T>` is public, and a consumer
  can append one directly; protection must hold for every append. The serializer is also the only
  place that already holds the owner (tenant, user) the stream resolved.
- *Alternative: a marked twin type* (`ProtectedFieldChangedEvent<T>` with `[EncryptData]` on
  `NewValue`), chosen by `ChangeSetHandler`. Rejected: it fixes only the change-set path, it encrypts
  at one fixed level instead of the property's own, and it adds a second event type every consumer's
  `Apply` methods and projections must handle.
- *Alternative: a published "value-carrier" seam* (an interface by which any payload declares that a
  value's protection is borrowed from another type's property). Rejected for now: one type needs it,
  and a public seam is a permanent contract. Revisit if a second carrier appears.

Evidence the decision rests on: the implementation (type-driven metadata, owner passed by
`EventSource`).

### 2. The owner and level are the snapshot's

Encryption uses the tenant and user `EventSource` already passes — the stream's resolved owner —
and the attribute's `DataSensitivityLevel`. That selects the same `KeyScope` as the property's value
in the snapshot, so an erasure that shreds the snapshot's copy shreds the recorded change too, and the
existing guards (`EnsureScopeCanIsolate`, `KeyScopeErasedException` from an erased subject) apply
unchanged. Nothing is added to `INamesDataOwner`; it governs commands, not events.

### 3. The AAD scope binds the value to the property name

The envelope's AAD scope is a fixed prefix plus the recorded property name (for example
`field-changed:<PropertyName>`; the literal is a design detail, chosen once and never changed). The
property name is in the clear and not otherwise authenticated by the encryption, so without the
binding an altered name would decrypt one property's value into another — the `data-encryption`
requirement "A protected recorded value is bound to the property it names". The prefix keeps the
scope distinct from the snapshot's bare property-name scope, so a snapshot ciphertext pasted into a
recorded change, or the reverse, does not decrypt either — the same principle as the purpose binding
of blob ciphertext (commit af323b9, PR #245, "bind blob ciphertext to its whole scope and purpose").

- *Alternative: the bare property name, as the snapshot uses.* Rejected for the cross-paste reason.
- *Alternative: also binding the aggregate type name.* Rejected: an aggregate type rename handled by
  an upcaster would then strand every protected change. The event type name already carries the
  aggregate type and is covered by the tamper-evident hash.
- *Consequence:* an upcaster that rewrites `PropertyName` makes the value unreadable (reading fails
  with the authentication error, not an absent value). A property rename on the aggregate already
  makes replay of the old name fail ("applying it fails rather than being silently ignored"), so this
  is a stricter form of an existing limit, not a new one. Recorded in the `event-schema-evolution`
  delta.

### 4. Already-stored plaintext is not rewritten, and is documented as exposure

The read path stays envelope-driven: a `NewValue` that is an envelope is decrypted; anything else is
taken as stored. That makes every earlier plaintext change replay unchanged, and keeps a value
readable if its property's mark is later removed.

A rewrite is not offered. The tamper-evident chain hashes each entry's payload with its position;
replacing a payload invalidates that entry's hash and every one after it, and destroys the evidence
the `tamper-evident-streams` capability preserves — and it would be the framework silently rewriting
facts. What is honest instead:

- The documentation states that changes recorded in the clear before this version are **existing
  exposure that an erasure does not reach**, on the same page that describes erasure.
- It gives the consumer a way to find them: the affected events are those of type
  `FieldChangedEvent<TAggregate>` whose `PropertyName` is a marked property of `TAggregate` and whose
  value is not an envelope — a query the guide shows against the event store, read-only.
- It states the options and their cost plainly: accept and record the exposure; or, as a deliberate
  operator act outside the framework, re-encrypt or null those payloads and re-chain and re-anchor
  from the first altered entry, accepting that the chain no longer proves those entries unchanged.

- *Alternative: a framework-shipped migration that re-encrypts and re-chains.* Rejected: it automates
  breaking the evidence, and it needs the original writer's owner resolution for every row.
- *Alternative: a read-only diagnostic that reports the affected events.* Not rejected — deferred to
  the owner (Open Questions).

### 5. Rolling upgrade: readers first, documented

A host on an earlier version deserializes `FieldChangedEvent<T>` as unmarked, so `NewValue` arrives as
the envelope object; `SetValueByName` then tries to convert that object to the property's type. For a
`string`, number, `Guid`, date, enum or collection property this throws `JsonException` and the replay
or the event handler fails — loud, and retried by the delivery machinery. For a property typed
`object`, `JsonElement` or a string-keyed dictionary the envelope would be **assigned silently**, and a
record or class property silently reads as an empty object. Implementation showed the record case; this
paragraph first claimed it failed loudly. Evidence: tests pinning the behaviour (task 2.6).

Recommended: one release that both reads and writes the protected form, with the reader-first upgrade
order documented (the `data-encryption` requirement "The upgrade order … is documented"), following the
precedent of the blob-format revision's "Rolling upgrades" section in `encrypt-data-setup.md`.

- *Alternative: two releases* — N reads the protected form, N+1 starts writing it. Safer for consumers
  with independently deployed services sharing a store or subscription; costs one release cycle of
  continued plaintext. Owner decision (Open Questions).
- *Alternative: a host option to keep writing plaintext.* Rejected: an off-switch for encryption of
  data the consumer marked as needing it is the kind of setting that stays off.

### 6. Refusing a change set that touches a marked property — considered, not recommended

`ApplyChangeSetAsync` (or `EventSource` on append) could throw when a field-changed event names a
marked property: loud instead of silent, a few lines, no format change, no rolling-upgrade question.

Not recommended, as the fix or as an interim step:
- It is a **breaking behaviour change** for every consumer whose update handlers already work (with
  the leak) on such aggregates — they would start failing in production on the next edit of that
  field, with no alternative inside the framework but writing bespoke events.
- It cannot be caught at start-up precisely: which update command pairs with which aggregate is known
  only at the call, so the failure arrives at run time, not on the first run.
- It leaves the stored plaintext problem (Decision 4) exactly as it is, and the real fix still has to
  be built.

It would be preferable only if the encryption fix were to be delayed by more than a release or two
**and** the owner judged a production failure better than continued plaintext for the consumers
affected. A cheaper interim, if one is wanted: a start-up **warning** naming each registered aggregate
that has marked properties and an `Apply(FieldChangedEvent<T>)` method. Owner decision.

### 7. The drift guard understands the borrowed protection

`EncryptionMetadataDriftGuard` compares `RequiresEncryption` with the type's own attributes. For a
closed `FieldChangedEvent<T>` the metadata will report encryption when `T` has marked properties while
the type itself carries no attribute; the guard must treat that as agreement (expected = `T` has any
marked property), not as drift. Evidence: the implementation of the guard.

## Risks / Trade-offs

- [An old reader assigns the envelope silently to an `object`-typed property] → documented in the
  upgrade section; covered by the reader-first order; a test pins the loud failure for typed
  properties.
- [Absent value for a non-nullable value type reads as `default`, indistinguishable from a recorded
  default] → same as every other protected field; the existing guidance to use nullable types for
  erasable fields is repeated on the update-handler page.
- [Per-instance metadata lookup on the append and read hot path] → one dictionary lookup of
  `PropertyName` in the cached aggregate metadata; events of other types are unaffected. A benchmark is
  not required; an existing benchmark run is compared if one covers appends.
- [The `NewValue` type on write is the aggregate property's declared type, on read a `JsonElement`] →
  that is already today's behaviour for plaintext; the conversion path in `SetValueByName` is shared.
- [Upcasters that rewrite `PropertyName` break on protected changes] → documented; rare, since an
  aggregate property rename already breaks replay of old changes.

## Migration Plan

1. Release with read and write support (or two releases, per the owner's decision on Decision 5).
2. Consumers upgrade every reading host and every event consumer first, then writers.
3. No schema migration, no data migration. Existing plaintext rows are documented exposure
   (Decision 4).
4. Rollback: a host rolled back to an earlier version fails to replay protected changes written in
   the meantime (Decision 5). Rolling back therefore requires rolling back every host together, or
   not at all — stated in the upgrade section.

## Implementation notes

- A property's own `[JsonConverter]` is not applied to the recorded value, unlike the snapshot: replay
  reads through the declared type alone, as it does for an unmarked property, so a converter's form
  would not read back.
- A value that is not an instance of the declared type, such as an array for a list, is written with
  its runtime type; change sets allow that shape.
- An aggregate marked for encryption as a whole gets no protection here. The drift guard already
  refuses such an aggregate at start-up where it is registered.

## Owner decisions (2026-11-06)

The owner approved this change for the coming release, ahead of the release itself.

1. **One release or two** (Decision 5): one release that reads and writes the protected form, with the
   reader-first upgrade order documented. No known consumer applies change sets.
2. **Interim step** (Decision 6): none — the fix ships in the release.
3. **Diagnostic for existing exposure** (Decision 4): documentation and a query only.
4. **Version bump**: decided with the release, together with the rest of its content.
