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
  level, whose key is destroyed through the test key store after the save (`RevokeAsync` on the key id
  in its payload), while the handled events carry no encrypted data. Assert the stream rebuilds.
  Confirm it fails against the unchanged code.

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

## 5. Review follow-ups

Copilot could not review the pull request (the requester's quota was exhausted), so an independent
review ran instead. What it found, and what was done:

- [x] 5.1 A handled type moved to another namespace was skipped silently where it used to fail. Keep
  an unresolvable entry whose name, without namespace or assembly, is the name of a handled type. Log
  every other unresolvable skip once per aggregate type and name (`LogEvents.EventStore.UnresolvableEventSkipped`).
  Covered by `AggregateEventSelectorTests` (moved and nested names kept, warning once) and
  `UnhandledEventRehydrationTests.A_handled_event_recorded_under_another_namespace_fails_the_rebuild_naming_it`.
- [x] 5.2 The first version read every event for any handler of an unsealed type, so a consumer with
  plain `public record` events kept the reported failure. For a resolvable type, ask the dispatcher's
  own `GetMethod` question. Only open aggregates (interface, abstract, `object`, generic) keep every
  unresolvable entry. Covered by `A_handler_taking_an_unsealed_record_takes_its_registered_subtypes_only`,
  `A_handler_taking_an_interface_takes_its_implementations_and_keeps_every_unresolvable_entry`, and the
  unsealed `CustomerOpened` in the store-backed tests.
- [x] 5.3 A replaced `IEventMapperFactory` may resolve names the selector cannot. Select only beside
  `EventMapperFactory`. Covered by `Beside_a_replaced_mapper_every_entry_is_read`.
- [x] 5.4 `AddEventSourcing()` also calls `AddEventUpcasterPipeline()` and `AddTrustedTypeResolver()`,
  and the selector's logger is optional, as `SecureJsonSerializer`'s is.
- [x] 5.5 Discovery registered `IEvent<TEvent>` instead of `TEvent` for an enveloped handler. Unwrap it
  in `TrustedTypeResolverServiceCollectionExtensions`. Covered by
  `Discovery_trusts_the_payload_of_a_handler_taking_the_enveloped_event`.
- [x] 5.6 Decide once per recorded name and aggregate type. Covered by `A_recorded_name_is_decided_once`.
- [x] 5.7 Align the spec delta, design, proposal and documentation with 5.1–5.6.

## 6. Gate

- [x] 6.1 `openspec validate leave-an-unhandled-event-unread --strict`
- [x] 6.2 `./scripts/local-gauntlet.sh`
