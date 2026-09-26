# Leave an irrelevant event unread

> **Status:** proposed

## Why

`leave-an-unhandled-event-unread`, merged for the next release, made rebuilding an aggregate skip an
event the aggregate has no handler for without reading it. The read side has the same fault, and nothing fixed it there.
The projection worker and the saga worker map every event of a bundle before they filter to the ones
a projection or saga declares a handler for. The projection replay does the same for every entry of
the store, the Orleans projection reader for every entry of its partition, and the saga reader's
relevance check resolves every entry's type. Mapping resolves the event's type against the host's
allowlist and decrypts its payload.

An event that no projection or saga in the host takes, and whose type the host never registered,
therefore fails where it should have been ignored:

- on the bus, the bundle fails and is dead-lettered, and with it every event in the bundle that a
  projection did take;
- in a replay, every attempt fails, after the read models were emptied;
- on the Orleans execution model, the partition stalls.

Such events are ordinary. One is an event type retired from an aggregate that no projection ever
handled. Another is a framework event, such as the tenant cascade, that only another host's
projections read. So is anything recorded in a stream the host was never meant to understand. Hosts
cope today by registering every event type of every domain assembly with
`AddDomainEventTypesFromAssemblyContaining<T>()`. That works only until an event has no `Apply`
anywhere, because that method registers only the types an `Apply` takes. The spec already promises
the right behaviour: a projection *is not invoked at all* for a bundle holding nothing it handles, and
a saga *reacts to nothing* it does not handle. Neither holds when the bundle cannot be read.

## What Changes

- The projection worker, the projection replay, the saga worker and the Orleans projection and saga
  readers map only the events whose type, after upcasting, a projection or saga in the host takes.
  Every other event is not resolved and not decrypted, and its type does not have to be registered.
- An event whose type does not resolve, but whose name without namespace or assembly is the name of a
  type a projection or saga in the host takes, is still read and still fails loudly. That is how a
  handled type moved without an upcaster has always shown up.
- A stateful saga process decides what it handles at run time, from the event. Its reader therefore
  maps every event whose type resolves, as today. It skips one that does not resolve, with a warning
  once per host and event type, instead of stalling the partition.
- `IEventMapperFactory` gains two overloads that take the host's relevance, one for stored entries and
  one for bus messages. Their default implementation maps everything and filters afterwards, which is
  today's behaviour, so a consumer's own mapper compiles and behaves as before. `EventRelevance`
  describes the relevance: a set of types, or any type that resolves.
- The worker-level debug log for a bundle nothing is relevant to stays. It now also covers a bundle
  whose events were all left unread.

No existing signature changes. Events a host handles are read exactly as before.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `projections`: *A projection declares the events it cares about by handling them* gains that an
  event no projection in the host handles is not read. Its type need not be registered, and it fails
  neither a bundle, a replay nor a store reader.
- `sagas`:
  - *A saga declares the events it reacts to by handling them* gains the same for sagas.
  - *A saga can be a stateful process with a correlation and a timeout* gains that a process's reader
    skips an event whose type does not resolve, with a warning.

## Impact

- `src/Stratara.Abstractions/Abstractions/EventSourcing/`: `EventRelevance` and the two
  `IEventMapperFactory` overloads with their default implementations.
- `src/Stratara.Shared/EventSourcing/Mapping/EventMapperFactory.cs`: the selective mapping, with the
  per-name decision cached.
- `src/Stratara.Projections/Services/ProjectionWorker.cs`, `ProjectionReplayWorker.cs`;
  `src/Stratara.Sagas/Services/SagaWorker.cs`; `src/Stratara.Orleans/Projections/ProjectionGrain.cs`,
  `src/Stratara.Orleans/Sagas/SagaReaderGrain.cs`: pass the host's relevance.
- `src/Stratara.Diagnostics/LogEvents.cs`: one warning for a process reader's unresolvable skip.
- Tests in `tests/Stratara.Shared.Tests`, `tests/Stratara.Projections.Tests`,
  `tests/Stratara.Sagas.Tests`, `tests/Stratara.Orleans.Tests`, `tests/Stratara.Testing.Orleans.Tests`.
- `docs/guides/write-a-projection.md` (the event-only-host section), `docs/guides/write-a-saga.md`, and
  `docs/reference/di-extensions-cheatsheet.md` (`AddDomainEventTypesFromAssemblyContaining`, which is
  no longer needed for events no handler takes).
- `CHANGELOG.md`. It ships in the same minor release as the unhandled-event and forgotten-tenant
  changes.
- Nothing is dissolved or superseded by this change.
