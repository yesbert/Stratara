## Context

See `proposal.md`, section Why. The relevant code, on `main` after #151–#154:

- `EventMapperFactory` (`src/Stratara.Shared/EventSourcing/Mapping/EventMapperFactory.cs`, singleton)
  maps a stored entry or a bus message the same way: upcast, then `ITrustedTypeResolver.Resolve`,
  which throws for an unregistered name, then decrypt and deserialize.
- Every read path maps before it filters:
  - `ProjectionWorker.ApplyAsync` maps the whole bundle, and `ProjectionManager` then filters per
    projection by relevant type names.
  - `ProjectionReplayWorker.ReplayBatchAsync` maps each entry, and the manager filters.
  - `SagaWorker.DispatchAsync` maps the whole bundle, and `SagaManager` filters.
  - `ProjectionGrain.ApplyBatchAsync` maps each entry, then filters by its cached relevant names.
  - `SagaReaderGrain` pre-filters a stateless saga's entries by stored type name. Its `Handles` calls
    `ITrustedTypeResolver.Resolve`, which throws for an unregistered name. For a process it maps
    everything, because `ISagaProcess.Handles(IEvent)` needs the mapped event.
- Dispatch is by exact type. `ProjectionMethodInvoker` and `SagaMethodInvoker` both find a handler with
  `ParameterType == eventType`, and the managers filter on the mapped event's exact qualified type name.
- Relevant types come from `IProjectionHandler.GetRelevantEventTypes` (since #153 this includes the
  deletion facts for a projection that forgets deleted tenants) and `ISagaHandler.GetRelevantEventTypes`.
- `IEventMapperFactory` is public and has two members. A consumer may replace it.

## Goals / Non-Goals

**Goals:**

- No read path resolves or decrypts an event that no handler in the host takes.
- A handled type moved without an upcaster keeps failing loudly.
- One implementation of the decision, not one per read path.

**Non-Goals:**

- Aggregates. `leave-an-unhandled-event-unread` covers them, and their rule is the dispatcher's
  binding rather than exact types.
- The command worker. A command is always handled by the handler it names.
- Changing what any `Add…FromAssemblyContaining` registers.
- Logging the skip of an irrelevant event for projections and stateless sagas. It is the normal case
  in any host that does not register every domain type, and the managers' debug log still covers a
  bundle nothing is relevant to.

## Decisions

### Selection lives in the mapper, behind a relevance argument

`IEventMapperFactory` gains:

```csharp
Task<IReadOnlyList<IEvent>> MapToEventsAsync(IEnumerable<EventStreamEntry> entries, EventRelevance relevance, CancellationToken cancellationToken = default);
Task<IReadOnlyList<IEvent>> MapToEventsAsync(IEnumerable<EventMessage> messages, EventRelevance relevance, CancellationToken cancellationToken = default);
```

Both are default interface members. They map everything with the existing overload, then keep the
events `relevance.Includes` accepts. A replaced mapper therefore compiles and behaves as today,
failures included. `EventMapperFactory` overrides them with the selective mapping below.

*Alternative rejected: a selector per read path, as the aggregate change has.* There are five read
paths across three packages. Every one of them needs the same upcast-then-resolve decision the mapper
already makes, and five copies would drift.

*Alternative rejected: reuse `AggregateEventSelector`.* Aggregates bind by assignability, and
projections and sagas dispatch by exact type. The selector also lives in `Stratara.Infrastructure`,
which none of these read paths reference.

### `EventRelevance`

It is a public sealed class in `Stratara.Abstractions.EventSourcing`, beside the mapper interface.

- `EventRelevance.ForTypes(IEnumerable<Type>)` stands for exact types. It exposes `Includes(Type)`
  and `MayName(string typeName)`, which compares against the types' `Name`.
- `EventRelevance.AnyResolvable` includes every type, and no unresolvable name.
  `EventRelevance.AnyResolvableWith(types)` includes every type too, and keeps an unresolvable name that
  equals one of `types`. It serves a process, whose `Handles` decides at run time, and keeps a type the
  process declares a handler for loud when it was moved without an upcaster.

### The decision for one entry or message, in `EventMapperFactory`

1. The effective name is the upcast name. The framework's pipeline chains upcasters by type name, so
   with it the effective name is cached per recorded name, and the extra upcast is paid once per name.
   A pipeline the host registered in its place may decide from the payload, so it is asked for every
   entry. Both this cache and the once-only warning set are capped at 4096 names, because the names
   can come off the bus.
2. When the effective name resolves to `T`, the event is mapped exactly when `relevance.Includes(T)`.
3. When it does not resolve, the event is mapped, and so fails in `Resolve` as today, when the name
   of the recorded type or of the effective type, without namespace or assembly, is the `Name` of a
   type the relevance names. Otherwise:
   - with `ForTypes`, it is skipped.
   - with `AnyResolvable…`, it is skipped, and the first skip per recorded name is logged at Warning
     (`LogEvents.EventStore.UnresolvableEventSkippedForAnyResolvable`, a new id beside 102 004). The
     mapper takes an optional `ILogger<EventMapperFactory>`. The logger is a singleton, so "once" means
     once per host.

A failure to resolve is not cached. Registrations only grow, so a name that resolves later is simply
decided again.

Evidence: the two invokers' exact-type match, `EventUpcasterPipeline.Upcast`, and the aggregate
change's cached decision, which the Orleans saga reader pioneered.

### The call sites

- `ProjectionWorker`, `ProjectionReplayWorker`: relevance is the union of `GetRelevantEventTypes` over
  the projections registered in the scope. The bus worker builds it once, because a host's projections
  do not change while it runs. The replay builds it per batch scope, which is cheap for an operation
  that rare.
- `SagaWorker`: the union over the sagas.
- **Only beside the framework's managers.** The workers select only when the resolved manager is the
  framework's `ProjectionManager` or `SagaManager`. A manager registered in its place is documented to
  receive the whole bundle and may dispatch to handlers of its own. It therefore gets every event
  through the older overload, as before. The review found that the first version handed such a
  manager a subset without a word.
- `ProjectionGrain`: its own projection's relevant types, cached beside `_relevant`.
- `SagaReaderGrain`: for a stateless saga, its relevant types through the mapper, which replaces the
  grain's own `Handles` pre-check and its throwing `Resolve`. For a process, `AnyResolvableWith` its
  declared types. `SagaReaderRun.Relevance` is built on first use. Once the grain knows its relevance,
  an entry is mapped before its run is entered, so an irrelevant one builds no scope. That moves
  decryption ahead of the run's session, which is harmless: the mapper takes the tenant from the entry,
  not from the session.

Each worker still hands the manager whatever was mapped, even an empty list. The managers' "not
relevant" debug log therefore keeps recording a bundle nothing is relevant to.

## Risks / Trade-offs

- [A handled type renamed without an upcaster is skipped on the read side, where it used to fail.] →
  Namespace, nesting and assembly moves keep the name and still fail. A pure rename for a projection
  or stateless saga is skipped silently. This is the one place the change trades a loud failure for
  silence, and the upgrade note names it. A warning per skip would fire for every irrelevant event in
  any host that does not register every domain type.
- [A consumer's own mapper keeps today's failure, and so does a decorator that forwards only the older
  overloads.] → The default members say so. A test double of `IEventMapperFactory` must set up the new
  overloads, or call its base, which is how this repository's own worker tests were adjusted. The
  upgrade note names both.
- [New public surface.] → It ships in the same minor release as the forgotten-tenant change.

## Migration Plan

None required. A host may drop `AddDomainEventTypesFromAssemblyContaining<T>()` calls that existed
only so that irrelevant events would not fail its projections or sagas. Rollback is reverting the
change.
