## Context

See `proposal.md`, section Why. The relevant code, as of 4.3.1:

- `AggregationService.AggregateAsync` (`src/Stratara.Infrastructure/EventSourcing/AggregationService.cs`)
  reads the stream's entries after the snapshot and hands all of them to
  `IEventMapperFactory.MapToEventsAsync`. Only then does `EventStream.ApplyEvents` look for a handler.
- `EventMapperFactory.MapToEventAsync` (`src/Stratara.Shared/EventSourcing/Mapping/EventMapperFactory.cs`)
  upcasts, then calls `ITrustedTypeResolver.Resolve`, which throws for an unregistered name, then
  deserializes through `ISecureJsonSerializer`. When an event was encrypted as a whole and its key is
  gone, the serializer returns `null`, and the mapper throws *Event data could not be deserialized*.
- `EventStream.CreateApplyDelegate` (`src/Stratara.Infrastructure/EventSourcing/EventStream.cs`) finds
  a handler with `Type.GetMethod("Apply", [eventType])`. That call binds public instance and static
  methods, and the default binder accepts a parameter the argument type is assignable to. A handler
  taking a base class or an interface therefore receives every derived event. The dispatcher tries
  the payload type first and falls back to `IEvent<TPayload>`.
- `SnapshotService.CreateSnapshot` rebuilds the aggregate through `IAggregationService`, then maps
  the entries being saved with the same mapper and applies them. `EventSource.SaveChangesAsync`
  calls it before the transaction commits. This is why a due snapshot turns an unregistered event
  into a failed save.
- `AddAggregatesFromAssemblyContaining<T>`
  (`src/Stratara.Abstractions/Abstractions/Reflections/TrustedTypeResolverServiceCollectionExtensions.cs`)
  registers each aggregate and the parameter type of every public instance `Apply` with one
  parameter. It does not unwrap `IEvent<T>`.
- The same mapper serves the projection worker, the replay, the saga worker and the Orleans grains.
  Each of them maps a whole bundle or entry before it filters.

## Goals / Non-Goals

**Goals:**

- Rebuilding an aggregate never resolves, deserializes or decrypts an event the aggregate does not
  handle.
- No event the aggregate might apply is ever skipped without a trace. Where that cannot be decided,
  rebuilding reads everything, exactly as today.
- No public surface changes, so the fix can ship as a patch.

**Non-Goals:**

- **Projections and sagas.** The projection worker, the replay, the saga worker and the Orleans
  projection and saga grains also map every event before they filter to the relevant ones. An
  irrelevant event whose type the host never registered fails them in the same way. That is a
  different capability (`projections`, `sagas`), on different packages, and it is not what a
  consumer hit. It is recorded as follow-up work, and this change does not touch those paths.
- Refusing to append an event the appending host could not read back. It would move this class of
  failure before the commit. With this change reading no longer depends on registration for an
  unhandled event, and the check would turn every append of a type the host never reads into a
  failure.
- Logging a skipped event. Skipping is the normal case for every aggregate that ignores a fact. The
  dispatcher skips silently today, and a log line per skipped event would be noise.
- Changing what `AddAggregatesFromAssemblyContaining<T>` registers.

## Decisions

### Select the entries before mapping, in the rehydration paths only

`AggregationService` and `SnapshotService` pass the entries through a new internal singleton,
`AggregateEventSelector`, before they call the mapper. The selector returns the entries the aggregate
reads, in their original order. The mapper, the resolver and every other read path are unchanged.

*Alternative rejected: a declaration in `Stratara.Domain` that the aggregate scan reads, so that
registering the tenant aggregates also trusts the cascade event.* It fixes the one event that
surfaced. It leaves the promise broken for every other unhandled event, the retired one included. The
owner chose the general fix on 2026-09-26, when asked between the two.

*Alternative rejected: skip inside the mapper when a type does not resolve.* The mapper serves every
read path. Skipping there would turn the allowlist's loud failure into silence for projections, sagas
and the bus, and the mapper does not know which aggregate is reading.

*Alternative rejected: a new `IEventMapperFactory` overload that takes a predicate.* It would upcast
once instead of twice (see Risks). But it is new public surface on an interface consumers can
implement, which makes this a minor release, and a patch should carry the fix. It remains the natural
shape if the projection follow-up needs the same selection.

*Alternative rejected: catch the resolution failure during rehydration and skip.* Without the type,
the rebuild cannot tell an unhandled event from a handled one whose registration is missing. It would
also skip on any failure the mapper throws.

Evidence: the implementation (`AggregationService.cs:48`, `EventMapperFactory.cs:49-57`), and the
reported stack trace, which runs from `AggregationService.AggregateAsync` through
`EventMapperFactory.MapToEventAsync` to `TrustedTypeResolver.Resolve`.

### A handler index per aggregate type, and a read-everything fallback

For each aggregate type, the selector builds a profile once and caches it in the singleton. It
collects every public method named `Apply` with one parameter, instance or static, which is what the
dispatcher binds. Each parameter type is unwrapped from `IEvent<T>` to `T`. The resulting set is the
aggregate's handled types.

The profile falls back to **read everything**, which is today's behaviour, when any of these holds:

- a handled type is not a sealed class or a struct: an interface, an abstract or unsealed class,
  `object`, or the non-generic `IEvent`. The dispatcher's assignable binding would hand such a
  handler events of other types.
- a handled type is generic, or an `Apply` is a generic method definition.
- a handled type does not resolve through `ITrustedTypeResolver` to itself. This means the aggregate
  was registered by some route other than discovery. An unresolvable recorded name could then be a
  type it handles.

The cache is safe to keep for the host's life. Registrations are only ever added, so a profile that
falls back cannot later become wrong, and a profile that selects can only have been built after its
handled types were registered.

*Alternative rejected: select on the recorded name alone, with no fallback.* An aggregate handling
an interface would silently lose every event whose type is registered under another name. The
convention is sealed records (`openspec/config.yaml`), but the framework must not corrupt state for a
consumer that departs from it.

Evidence: the implementation of the dispatcher (`EventStream.cs`, `CreateApplyDelegate`), and the
convention in `openspec/config.yaml`: *Events are immutable sealed records*.

### The decision for one entry

For an aggregate whose profile selects, an entry is read when:

1. its recorded name resolves to a handled type. It is kept without running the upcasters here,
   because the mapper upcasts it as before.
2. Otherwise the upcaster pipeline gives its effective name. If that resolves, the entry is kept
   exactly when the resolved type is handled.
3. If the effective name does not resolve, the entry is kept, and therefore fails in the mapper as
   today, when its type part (the name before the first comma) equals the full name of a handled
   type. That is the moved-assembly case. Every other unresolvable entry is skipped.

Step 3's plain split at the first comma is enough. Handled types in a selecting profile are never
generic, so a generic recorded name can equal no handled full name however it is split. The
version-independent normalisation in `Stratara.Abstractions` is internal, and none of it is needed
here.

Evidence: `EventUpcasterPipeline.Upcast` returns its inputs unchanged, without parsing, when no
upcaster matches (`src/Stratara.Abstractions/Abstractions/EventSourcing/EventUpcasterPipeline.cs`).

### Wiring

`AddEventSourcing()` registers the selector with `TryAddSingleton`. Its dependencies,
`ITrustedTypeResolver` and `IEventUpcasterPipeline`, are already required wherever the rehydration
paths resolve: `SnapshotService` takes the resolver, and `EventMapperFactory` takes the pipeline.
`AggregationService` and `SnapshotService` gain the selector as a constructor parameter. Both are
internal, so no public signature changes.

## Risks / Trade-offs

- [An upcast entry the aggregate does not handle directly is upcast twice: once to decide, once in
  the mapper.] → Only entries with a matching upcaster pay, and those are old events, which snapshots
  keep out of most rebuilds. With no upcaster registered the pipeline returns at once. The predicate
  overload that would remove the cost is the rejected public-surface alternative above.
- [A consumer relies on the rebuild failing as a check that every type in a stream is registered.] →
  That was never a documented guarantee, and it held only for unhandled events. Handled types stay
  registered by discovery, and every other read path still enforces the allowlist.
- [The profile misjudges what the dispatcher binds, and an event the aggregate would apply is
  skipped.] → The profile collects a superset of what `GetMethod` can bind. Any doubt about
  assignability falls back to reading everything. Unit tests pin one case each: an interface handler,
  an unsealed record, `IEvent<T>` unwrapping, a static `Apply`, and an aggregate registered without
  discovery.
- [A type registered after the host started.] → Registrations only grow, and a profile built before a
  registration falls back to reading everything. This is safe, and at worst it is today's behaviour
  until restart.

## Migration Plan

None for consumers. A consumer that worked around the failure can keep its workaround, whether a
no-op handler or an explicit registration, or drop it. Rollback is reverting the patch.
