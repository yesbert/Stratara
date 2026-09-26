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
- No event the aggregate might apply is skipped without a trace. For a type that resolves, the
  decision is the dispatcher's own. For one that does not, it is read where it could be handled, and
  otherwise the skip is logged.
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
- Logging the skip of an event whose type resolves. Skipping is the normal case for every aggregate
  that ignores a fact, and the dispatcher skips such events silently today. Only an unresolvable skip
  is logged, once per host, aggregate type and name.
- Registering more than the payload types of the handlers. Discovery changes only in unwrapping
  `IEvent<TEvent>`.

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

*Alternative rejected: a new `IEventMapperFactory` overload that takes a predicate.* It would save
the one extra upcast per recorded name (see Risks). But it is new public surface on an interface consumers can
implement, which makes this a minor release, and a patch should carry the fix. It remains the natural
shape if the projection follow-up needs the same selection.

*Alternative rejected: catch the resolution failure during rehydration and skip.* Without the type,
the rebuild cannot tell an unhandled event from a handled one whose registration is missing. It would
also skip on any failure the mapper throws.

Evidence: the implementation (`AggregationService.cs:48`, `EventMapperFactory.cs:49-57`), and the
reported stack trace, which runs from `AggregationService.AggregateAsync` through
`EventMapperFactory.MapToEventAsync` to `TrustedTypeResolver.Resolve`.

### For a type that resolves, ask the dispatcher's question

For an entry whose type resolves after upcasting, the selector asks exactly what `EventStream` asks
when it applies an event. That is `aggregateType.GetMethod("Apply", [T])`, then the same call for
`IEvent<T>`, and the entry is read when either binds. No approximation of the binder is involved:
assignability, covariance of `IEvent<out T>`, interfaces, base classes and static handlers bind in
the selector exactly as they do in the dispatcher. An `AmbiguousMatchException` counts as binding, so
the dispatcher fails as it did before. The answer is cached per aggregate type and event type.

*Alternative rejected, and the first version of this change: a set of handled types, with a
read-everything fallback for any handler that is not a sealed class or a struct.* An independent
review pointed out that a plain `public record` is unsealed. For a consumer following common C# style
rather than this repository's convention, the fallback applied and the reported failure stayed.
Asking the dispatcher's question is both exact and simpler.

Evidence: the dispatcher (`EventStream.cs`, `CreateApplyDelegate`), and the unit tests that pin an
interface handler, an unsealed record with a registered subtype, an enveloped handler and a static
handler.

### For a type that does not resolve, keep what could be handled, and warn about the rest

An unresolvable entry cannot be asked about. It is read, and therefore fails in the mapper as an
unregistered type does, when:

1. its type name, without namespace or assembly, is the `Name` of a type an `Apply` takes, after
   unwrapping `IEvent<T>`. This covers a handled type moved to another namespace, nested differently,
   or moved to another assembly, which are the refactorings that most often forget an upcaster. It
   also covers a handled type that is not registered at all, because the aggregate was registered some
   other way than by discovery.
2. the aggregate is open: an `Apply` takes an interface, an abstract class, `object` or a generic type,
   or is itself generic. Any unregistered type could then be one it applies.

A recorded name with a generic argument list is always kept. Every other unresolvable entry is
skipped, and the first skip per aggregate type and recorded name is logged at Warning
(`LogEvents.EventStore.UnresolvableEventSkipped`, 102_004), naming both and saying how to register or
upcast the type, or how to acknowledge the skip by registering it.

*Why a warning and not silence:* the review showed that the first version dropped a renamed handled
event silently where the old code failed loudly, and that a due snapshot would then persist the
wrong state. The name rule restores the loud failure for namespace and assembly moves. For a type
renamed without an upcaster no rule can tell, so the warning leaves the trace. A legitimately
unhandled event is acknowledged by registering its type, after which it resolves and is skipped
without a word.

### Decisions are made once per recorded name

The upcasters chain by type name, so everything above depends on the recorded name alone. The
profile caches the decision per recorded name, as the Orleans saga reader already caches its
relevance check. Registrations are only ever added, so a cached decision cannot become unsafe. It can
at worst stay a "read" after a later registration would have allowed a skip.

### Only beside the framework's mapper

The selector selects only when the registered `IEventMapperFactory` is `EventMapperFactory`. A
replaced mapper may resolve names through rules the selector does not know, such as an alias map, so
beside one every entry is read, as before.

### Wiring

`AddEventSourcing()` registers the selector with `TryAddSingleton`. It also calls
`AddEventUpcasterPipeline()` and `AddTrustedTypeResolver()`, both idempotent, so a host that brings its
own mapper and never calls `AddMapping` still resolves `IAggregationService`. The logger is optional,
as it is for `SecureJsonSerializer`, because a bare composition without logging must keep working.
`AggregationService` and `SnapshotService` gain the selector as a constructor parameter. Both are
internal, so no public signature changes.

### Discovery trusts the payload of an enveloped handler

`AddAggregatesFromAssemblyContaining<T>` and `AddDomainEventTypesFromAssemblyContaining<T>` used to
register the parameter type of `Apply(IEvent<TEvent>)`, which is `IEvent<TEvent>`. No recorded event
names that type. They now register `TEvent`. The review found this while checking the name rule. It
is a separate, pre-existing gap: an aggregate whose only handler for an event took the envelope could
not rebuild its stream unless something else registered the payload.

## Risks / Trade-offs

- [A handled type renamed without an upcaster is skipped with a warning instead of failing the
  rebuild, and a due snapshot keeps the resulting state.] → Namespace, nesting and assembly moves keep
  the name and still fail loudly. A pure rename leaves a Warning per host, aggregate and name. The
  upgrade note says so.
- [A legitimately unhandled, unregistered event logs one Warning per host, aggregate type and event
  type.] → Registering the type acknowledges it, and the message says how.
- [An upcast entry is upcast once more to decide than it would be to map.] → Once per recorded name
  and aggregate type, not per entry. With no upcaster registered, the pipeline returns at once.
- [A throwing upcaster on an unhandled event still fails the rebuild.] → As it did before. The
  requirement promises that the skipped event's type is not resolved and its payload not decrypted;
  running the upcasters to learn its type is neither.
- [A consumer relies on the rebuild failing as a check that every type in a stream is registered.] →
  That was never a documented guarantee, and it held only for unhandled events. Every other read path
  still enforces the allowlist.

## Migration Plan

None for consumers. A consumer that worked around the failure can keep its workaround, whether a
no-op handler or an explicit registration, or drop it. Rollback is reverting the patch.
