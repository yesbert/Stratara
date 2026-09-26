## 1. Reproduce first

- [x] 1.1 Add `tests/Stratara.Infrastructure.Tests/EventSourcing/UnhandledEventRehydrationTests.cs`,
  store-backed through `EventStoreTestHost`. A test-local aggregate stands in for a consumer's
  customer, registered with `AddAggregatesFromAssemblyContaining` of a marker that does not bring in
  `Stratara.Domain`. Its stream holds a handled creation event, then `CustomerTenantsDeleted` from
  `Stratara.Domain`, then a handled follow-up. Assert that `AggregateAsync` returns the aggregate
  with both handled events applied. Run it against the unchanged code and confirm it fails with
  the trusted-type resolver's message.
- [x] 1.2 In the same file, a retired-type case: a test-local event record that no aggregate in the
  scanned assembly handles and that nothing registers. Assert the stream rebuilds.
- [x] 1.3 In the same file, the snapshot case: register an `ISnapshotStrategy` that always snapshots,
  as `SnapshotTypeIsolationTests` does. Save a handled event together with an unregistered, unhandled
  one. Assert that the save succeeds, that a snapshot exists, and that the aggregate rebuilds from it.
- [x] 1.4 In the same file, the erased-key case: an unhandled event declared `[EncryptData]` at class
  level, whose tenant scope is erased through the test key store's `EraseScopeAsync` after the save,
  while the handled events carry no encrypted data. Assert the stream rebuilds. Confirm it fails
  against the unchanged code.

## 2. The selector

- [x] 2.1 Add `src/Stratara.Infrastructure/EventSourcing/AggregateEventSelector.cs` as an internal
  sealed class, taking `ITrustedTypeResolver` and `IEventUpcasterPipeline`. It builds and caches the
  per-aggregate-type profile and applies the per-entry rule, both as `design.md` describes. It
  preserves entry order.
- [x] 2.2 Add `tests/Stratara.Infrastructure.Tests/EventSourcing/AggregateEventSelectorTests.cs`, using
  a real `TrustedTypeResolver` and a real `EventUpcasterPipeline`, one test per rule:
  - a registered unhandled entry is dropped;
  - an unregistered unhandled entry is dropped;
  - a handled entry is kept;
  - an `IEvent<T>` handler counts as handling `T`;
  - an entry upcast into a handled type is kept;
  - an unresolvable name whose type part equals a handled type's full name is kept;
  - an interface-typed handler makes the profile read everything;
  - an unsealed-record handler makes the profile read everything;
  - a static `Apply` is counted;
  - a generic `Apply` makes the profile read everything;
  - a handled type missing from the resolver makes the profile read everything.
- [x] 2.3 Register the selector in `AddEventSourcing()`
  (`src/Stratara.Infrastructure/DependencyInjection/EventSourcingServiceCollectionExtensions.cs`)
  with `TryAddSingleton`.

## 3. The rehydration paths

- [x] 3.1 `AggregationService` takes the selector and maps only the entries it returns for the
  requested aggregate type, on both the snapshot and the no-snapshot path. Update the constructor in
  `tests/Stratara.Infrastructure.Tests/EventSourcing/AggregationServiceTests.cs`, and add a case
  asserting the mapper receives only the selected entries.
- [x] 3.2 `SnapshotService.CreateSnapshot` passes the entries being saved through the selector for the
  resolved aggregate type before mapping them. Update the constructor in
  `tests/Stratara.Infrastructure.Tests/EventSourcing/SnapshotServiceTests.cs`.
- [x] 3.3 Tasks 1.1 to 1.4 pass, and the existing tests in `tests/Stratara.Infrastructure.Tests`,
  `tests/Stratara.Testing.EntityFrameworkCore.Tests` and `tests/Stratara.Orleans.Tests` still pass.

## 4. Documentation

- [x] 4.1 `src/Stratara.Domain/README.md`: `CustomerTenantsDeleted` belongs on the consumer's own
  stream. Rebuilding that aggregate skips it without reading it, so it needs neither a handler nor
  a registration there. Drop the claim that holds only for a registered type.
- [x] 4.2 `docs/guides/configure-snapshots.md`, *What a rebuild applies, and what it skips*: an
  unhandled event is skipped without being read, so its type need not be registered. Name the
  cases in which a rebuild reads every event.
- [x] 4.3 `docs/guides/tenant-membership.md`: one sentence at the `CustomerTenantsDeleted` mention
  saying where the event goes and that the consumer's aggregate needs nothing for it.
- [x] 4.4 Add the entry to `CHANGELOG.md` under Unreleased as a fix. Name the failure message a
  consumer may have seen.

## 5. Gate

- [x] 5.1 `openspec validate leave-an-unhandled-event-unread --strict`
- [x] 5.2 `./scripts/local-gauntlet.sh`
