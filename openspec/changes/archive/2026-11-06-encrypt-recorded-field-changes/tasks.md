# Tasks

Before starting: the owner's decisions on design.md Open Questions 1 to 3 decide whether the write
side (group 1) ships in the same release as the read side (group 2), and whether an interim start-up
warning or a read-only diagnostic is added as tasks of their own. Do not start until the Status line of
proposal.md reads `approved`.

## 1. Protect the recorded value on write

- [x] 1.1 Extend the encryption metadata so a closed `FieldChangedEvent<TAggregate>` carries the
  marked-property map of `TAggregate` (property and primary-constructor parameter marks, from the same
  `MetadataCache` entry the snapshot uses) and reports `RequiresEncryption` when that map is non-empty
  (`src/Stratara.Infrastructure/Security/Serialization/MetadataCache.cs`, `EncryptionMetadata.cs`).
  Verify: a new test in `tests/Stratara.Infrastructure.Tests/Security/SecureJsonSerializerTests.cs`
  asserts the metadata of `FieldChangedEvent<T>` for an aggregate with and without marked properties.
- [x] 1.2 In `SecureJsonSerializer.SerializeAsync`, for a `FieldChangedEvent<TAggregate>` whose
  `PropertyName` is marked on `TAggregate`, write `PropertyName` plain and `NewValue` as an envelope:
  serialized with the aggregate property's declared type, at the attribute's level, for the passed
  tenant and user, with the AAD scope fixed in design.md Decision 3. A `null` value is written as
  `null`, as for any marked property. Verify: tests in `SecureJsonSerializerTests` — the serialized
  JSON has `"__enc":true` under `NewValue`, the plaintext appears nowhere in it, and the key scope
  requested from the test key store equals the one the aggregate's own serialization requests.
- [x] 1.3 Leave unmarked properties untouched. Verify: a test that a change to an unmarked property of
  an aggregate that has marked properties serializes byte-identical to `JsonSerializer.Serialize` and
  performs no key-store call (counting `InMemoryKeyStore` or a spy).
- [x] 1.4 Teach `EncryptionMetadataDriftGuard` that a `FieldChangedEvent<T>` borrowing protection from
  `T` is agreement, not drift (design.md Decision 7). Verify: a new case in
  `tests/Stratara.Infrastructure.Tests/Security/EncryptionMetadataDriftGuardTests.cs` starts a guard
  whose trusted types include `FieldChangedEvent<T>` for a marked aggregate.

## 2. Read it back, degrade, and stay compatible

- [x] 2.1 In `SecureJsonSerializer.DeserializeAsync`, decrypt a `NewValue` envelope of a
  `FieldChangedEvent<T>` under the AAD built from the recorded `PropertyName`, and pass a non-envelope
  `NewValue` through unchanged. Decide by envelope presence, not by the property's current mark.
  Verify: `SecureJsonSerializerTests` round-trip a `string`, a nullable `int`, a non-nullable `Guid`
  and a record-typed marked property, and read a hand-written plaintext payload for a marked property.
- [x] 2.2 Unrecoverable key → `NewValue` absent. Verify: tests that revoke the key version and erase the
  subject in `InMemoryKeyStore`, then deserialize: `NewValue` is `null`, `PropertyName` is intact, no
  exception.
- [x] 2.3 Applying an absent value sets null for reference and nullable types and `default` for a
  non-nullable value type (`FieldChangedEventExtensions.ApplyPropertyChanged` →
  `PropertyAccessorCache.SetValueByName`). Verify: tests in
  `tests/Stratara.Shared.Tests/FieldChangedEventExtensionsTests.cs`; change the code only if a test
  fails.
- [x] 2.4 Binding: altering the stored `PropertyName` of a protected change makes reading fail with the
  authentication error. Verify: a test in `SecureJsonSerializerTests` that rewrites the name in the
  JSON (to another marked property and to an unmarked one) and asserts the exception.
- [x] 2.5 A snapshot envelope pasted into a recorded change, and the reverse, does not decrypt. Verify:
  a test in `SecureJsonSerializerTests`.
- [x] 2.6 Pin what an earlier reader does: a test deserializing a protected payload through plain
  `JsonSerializer` (as an unmarked type) and applying it to a `string` property throws `JsonException`.
  Verify: the test, in `FieldChangedEventExtensionsTests`; it is the evidence the upgrade section of the
  documentation cites.

## 3. Round-trip through the store and the bus

- [x] 3.1 End-to-end through the event source with the test event store: create an aggregate with a
  marked and an unmarked property, apply a change set through `IChangeSetHandler` that changes both,
  read the raw stored `DataJson` (ciphertext for the marked value, plaintext for the other), and
  rebuild the aggregate through `IAggregationService` (both values back). Verify: a new test class in
  `tests/Stratara.Infrastructure.Tests/EventSourcing/` next to `ChangeSetHandlerTests.cs`, using
  `Stratara.Testing.EntityFrameworkCore`.
- [x] 3.2 Same fixture: erase the stream's owner through the subject eraser, rebuild — the marked
  property is absent, the unmarked one intact, later events still applied. Verify: the same test class.
- [x] 3.3 Same fixture: a stream holding a plaintext field change for a marked property (inserted raw,
  as an earlier version wrote it) followed by a protected one rebuilds to the protected value; the
  plaintext row is unchanged after the rebuild. Verify: the same test class.
- [x] 3.4 Snapshot parity: with snapshots enabled, a rebuild from the snapshot and a rebuild from the
  events alone give the same value before and after erasure. Verify: the same test class.
- [x] 3.5 Bus: an `EventMessage` built from the stored entry carries the envelope, and mapping it
  through `EventMapperFactory` recovers the value. Verify: a test in
  `tests/Stratara.Shared.Tests/EventMapperFactoryTests.cs` mapping a message built from a stored entry. Stated in the
  spec scenario as verified on the mapper, not on a live broker.
- [x] 3.6 Upcaster view: an upcaster registered for `FieldChangedEvent<T>` receives the envelope under
  `NewValue` and the plain `PropertyName`, and one that rewrites `PropertyName` makes mapping fail.
  Verify: tests in `tests/Stratara.Shared.Tests/EventMapperFactoryTests.cs`.
- [x] 3.7 The Orleans packages do not reference `FieldChangedEvent` (checked when this change was
  drafted); confirm it still holds and that grain appends go through the same serializer. Verify:
  `grep -rn FieldChangedEvent src/Stratara.Orleans*` returns nothing, stated in the PR.

## 4. Documentation (derived pages)

- [x] 4.1 `docs/guides/encrypt-data-setup.md`: a section that changes to a marked property recorded by
  an update are protected like the property; a "Rolling upgrades" subsection for the reader-first
  order, the failure an earlier reader shows (citing 2.6), and the silent case for `object`-typed
  properties; a section on field changes recorded in the clear before this version — existing
  exposure that erasure does not reach, the read-only query that finds them, and the options of
  design.md Decision 4 including what re-chaining costs. Verify: `tests/Stratara.Documentation.Tests`
  pass (snippet and link checks) and the page's derived-page banner still names `data-encryption`.
- [x] 4.2 `docs/guides/write-an-update-handler.md`: a short section that a change to a marked property
  is recorded protected, reads absent after erasure, and that erasable fields should be nullable.
  Verify: documentation tests.
- [x] 4.3 `docs/guides/evolve-an-event-schema.md`: an upcaster sees ciphertext for a protected recorded
  change and must not rewrite its property name. Verify: documentation tests.
- [x] 4.4 `docs/concepts/tenant-aware-encryption.md`: add recorded field changes to what is protected,
  and the pre-existing plaintext to the "what it does not protect" list. Verify: documentation tests.
- [x] 4.5 XML documentation of `FieldChangedEvent<TAggregate>` states that `NewValue` is protected when
  the named property is marked (`src/Stratara.Shared/EventSourcing/FieldChangedEvent.cs`). Verify:
  the build with CS1591 as error.

## 5. Integration check

- [x] 5.1 Run the touched unit slices (`tests/Stratara.Infrastructure.Tests`, `tests/Stratara.Shared.Tests`,
  `tests/Stratara.Documentation.Tests`) and `./scripts/local-gauntlet.sh`. Verify: all green.
- [x] 5.2 `openspec validate encrypt-recorded-field-changes --strict` passes. Verify: the command's
  output.

## Workflow follow-up

- CHANGELOG entry at release time, naming the exposure of earlier plaintext changes and the upgrade
  order.
- Archive the change with `/opsx:archive` after the PR merges; per the project's habit it may ride
  with the next change branch.
