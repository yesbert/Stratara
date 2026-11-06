# Encrypt recorded field changes

> **Status:** approved

## Why

The consumer-visible effect today: a value written to a property marked `[EncryptData]` is
ciphertext in the aggregate's snapshot and in the command audit, but **plaintext** in the event
stream and on the bus whenever the update was applied as a change set. Every changed field becomes
a `FieldChangedEvent<TAggregate>(PropertyName, NewValue)`, and encryption is decided by the marks on
the type being serialized — the field-changed event carries none, so its `NewValue` is written as
ordinary JSON whatever property it names. Erasing the tenant's or the user's key does not reach that
copy, so the erasure promise of the `data-encryption` capability is broken for exactly the data a
consumer marked as needing it. The combination is expected, not exotic: the `observability` spec
already describes logging "a change set covering fields marked for encryption".

The defect was found in a code review and deliberately left unfixed: the fix changes what is stored,
how older hosts read it, and how upcasters see it, which are the owner's decisions.

## What Changes

- **A recorded change to a marked property is protected as that property is.** The value a
  field-changed event records for a property marked for encryption on the aggregate is stored and
  published as ciphertext — at the property's own sensitivity level, for the stream's owner (the
  same tenant and user its other protected fields and its snapshot are encrypted for). The name of
  the changed property stays readable. This holds for every field-changed event the framework
  appends, whether produced by a change set or appended by the consumer directly.
- **It reads back on replay and degrades on erasure.** Rebuilding an aggregate or a projection sets
  the property to the recorded value. Once the key is destroyed — a revoked version, an erased scope
  or an erased subject — the value reads as absent (null, or the default of a non-nullable value
  type) and the replay continues, exactly as for any other unreadable protected field.
- **The protected value is bound to the property it names**, so a stored change whose property name
  was altered fails to decrypt rather than writing one property's value into another.
- **Changes to unmarked properties are stored exactly as today.** No key lookup, no format change.
- **Changes already recorded in the clear stay as they are and keep replaying.** The framework does
  not rewrite them: streams are tamper-evident, and a rewrite would break the chain that is their
  evidence. The documentation states the exposure honestly — erasure does not reach those values —
  and tells a consumer how to find the affected events and what its options are.
- **Rolling upgrade (BREAKING for mixed-version readers).** A host on an earlier version cannot read
  a protected recorded change written by this version: for a typed property it fails loudly when
  the ciphertext cannot be converted to the property's type. The documentation states the upgrade
  order: every host that reads the stream or consumes its events first.
- **Upcasters see ciphertext** for the value of a protected recorded change, and the readable
  property name; they can still route or rename the event type, but cannot inspect or transform the
  value, and cannot rename the property the change names without making its value unreadable.
- Considered and not recommended as the fix: refusing a change set that touches a marked property.
  See design.md, Decision 6.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `data-encryption`: adds that a recorded field change inherits the protection of the property it
  names, is bound to it, and that changes recorded in the clear before this version are read as they
  are, not rewritten, and documented as exposure that erasure does not reach.
- `update-change-tracking`: the requirement that a field-changed event names the field and carries
  its new value gains the protected case — the value replays when readable, reads as absent once its
  key is destroyed, and an earlier plaintext record still replays.
- `event-schema-evolution`: the requirement that upcasting operates on the payload as stored gains
  the scenario of a protected recorded change.

## Impact

- **Code (implementation phase, not this change):**
  `src/Stratara.Infrastructure/Security/Serialization/SecureJsonSerializer.cs`,
  `src/Stratara.Infrastructure/Security/Serialization/MetadataCache.cs`,
  `src/Stratara.Infrastructure/Security/Serialization/EncryptionMetadata.cs`,
  `src/Stratara.Infrastructure/Security/Serialization/EncryptionMetadataDriftGuard.cs`;
  `src/Stratara.Shared/EventSourcing/FieldChangedEvent.cs` (XML documentation only — the record's
  shape does not change); `src/Stratara.Shared/EventSourcing/Extensions/FieldChangedEventExtensions.cs`
  and `src/Stratara.Shared/Reflections/PropertyAccessorCache.cs` (absent-value application, verified,
  changed only if a test shows a gap). `ChangeSetHandler` does not change.
- **Stored format:** new field-changed events for marked properties carry an encrypted envelope in
  place of `NewValue`'s plain JSON. Existing rows are untouched. No schema migration.
- **Published API:** none added or removed. `FieldChangedEvent<TAggregate>` keeps its signature.
- **Documentation (derived pages):** `docs/guides/encrypt-data-setup.md`,
  `docs/concepts/tenant-aware-encryption.md`, `docs/guides/write-an-update-handler.md`,
  `docs/guides/evolve-an-event-schema.md`; `CHANGELOG.md` at release time.
- **Dissolved or superseded sources:** none.
