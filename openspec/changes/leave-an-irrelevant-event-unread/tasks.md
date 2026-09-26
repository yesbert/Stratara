## 1. Reproduce first

- [x] 1.1 In `tests/Stratara.Projections.Tests/Services/ProjectionReplayWorkerTests.cs`, a replay through
  a real `EventMapperFactory` (real resolver, upcaster pipeline and a pass-through serializer) over a
  store holding an event no projection handles, of a type the resolver never registered. Assert the
  replay completes. Confirm it fails on `main` with the trusted-type resolver's message.
- [x] 1.2 In `tests/Stratara.Testing.Orleans.Tests`, a store-reading projection whose partition holds such
  an event. Assert `WaitForReadersAsync` completes. Confirm it stalls on `main`.

## 2. The mapper

- [x] 2.1 Add `EventRelevance` (`ForTypes`, `AnyResolvable`, `Includes`, `MayName`) and the two
  `IEventMapperFactory` overloads, with default implementations that map everything and filter, under
  `src/Stratara.Abstractions/Abstractions/EventSourcing/`. Full XML documentation.
- [x] 2.2 Implement them in `EventMapperFactory`, as `design.md` describes, with the effective name
  cached per recorded name and an optional `ILogger<EventMapperFactory>`. Add
  `LogEvents.EventStore.UnresolvableEventSkippedForAnyResolvable`.
- [x] 2.3 `tests/Stratara.Shared.Tests`, one test per rule:
  - a relevant resolvable event is mapped;
  - an irrelevant resolvable one is not deserialized;
  - an irrelevant unregistered one is skipped;
  - an unregistered one with a relevant type's name fails;
  - an event upcast into a relevant type is mapped;
  - `AnyResolvable` maps every resolvable event and skips and warns once for an unresolvable one;
  - the default interface members keep today's behaviour for a mapper that does not override them;
  - both the entry and the message overloads are covered.

## 3. The read paths

- [x] 3.1 `ProjectionWorker` and `ProjectionReplayWorker` pass the union of their projections' relevant
  types. Cover a bundle with an unregistered irrelevant event in `ProjectionWorkerTests`.
- [x] 3.2 `SagaWorker` passes the union of its sagas' relevant types. Cover the same case in
  `SagaWorkerTests`.
- [x] 3.3 `ProjectionGrain` passes its projection's relevant types. `SagaReaderGrain` passes a stateless
  saga's relevant types through the mapper, instead of its own `Handles` and `Resolve`, and passes
  `AnyResolvable` for a process. Update the tests in `tests/Stratara.Orleans.Tests` that pin the old
  pre-check.
- [x] 3.4 Tasks 1.1 and 1.2 pass, and every test project that references the changed packages still
  passes.

## 4. Documentation

- [x] 4.1 `docs/guides/write-a-projection.md`: an event no projection handles is not read, and its type
  need not be registered. Correct the event-only-host section, which currently implies the whole domain
  must be registered.
- [x] 4.2 `docs/guides/write-a-saga.md`: the same for sagas, and the process reader's warning.
- [x] 4.3 `docs/reference/di-extensions-cheatsheet.md`: `AddDomainEventTypesFromAssemblyContaining` is no
  longer needed to keep irrelevant events from failing a host.
- [x] 4.4 `CHANGELOG.md` under Unreleased: Fixed, Added (`EventRelevance` and the overloads), and an
  Upgrading note on renamed handled types.

## 5. Gate

- [x] 5.1 `openspec validate leave-an-irrelevant-event-unread --strict`
- [ ] 5.2 `./scripts/local-gauntlet.sh`
