# Leave an unhandled event unread

> **Status:** approved

## Why

A consumer deleted a customer on 4.3.0. Its command handler appended the tenant package's cascade
event, the one that records every tenant of a customer as deleted, to the consumer's own customer
stream, and the deletion then stopped at a later step, as designed. Every retry failed before it
could resume: *Type `…CustomerTenantsDeleted…` is not registered in the trusted-type resolver*, thrown
while rebuilding the customer aggregate. The customer aggregate declares no handler for that event.
The capability says such an event is skipped. It was not skipped: rebuilding reads, resolves and
decrypts every event in the stream before it looks for a handler. An event whose type the host never
registered fails at the first of those steps, and the aggregate that would have ignored it never
sees it.

The cascade event is only the case that surfaced. Registration by aggregate discovery trusts the
types an aggregate's handlers take and nothing else. So every unhandled event in a stream is one
missing registration away from stopping the rebuild. That includes an event the aggregate stopped
handling, which is exactly the case the capability names as the reason for skipping. The snapshot
guide's own example, an aggregate with no handler for withdrawals, fails this way in a host that
registers only that aggregate. A host that also runs a projection over the same events does not fail,
because the projection's registration covers it. That is why the gap went unnoticed.

Two things make it worse than a wrong message. It fails after the fact: the event was appended and
committed without complaint, and only the next rebuild of that stream refuses it. A snapshot that is
due at the moment of the append fails the save itself. It also reads what it will throw away:
rebuilding decrypts every unhandled event's payload. An event encrypted as a whole under a key that
has since been erased therefore stops the rebuild of an aggregate that never looks at it.

## What Changes

- Rebuilding an aggregate skips an event the aggregate declares no handler for **without resolving
  its type or decrypting its payload**. It does not require the type to be registered. Whether the
  aggregate handles an event is decided on its type after upcasting, and by the same binding the
  aggregate's handlers are applied with. An event upcast into a handled type is applied, and so is a
  registered event a handler takes through a base type or an interface.
- This holds for every rebuild: loading an aggregate, with or without a snapshot, and the rebuild
  that takes a snapshot while events are being saved.
- An event whose type does not resolve, but which the aggregate might handle, is never skipped. It
  fails as it does today. That is the case when:
  - its name, without namespace or assembly, is the name of a type a handler takes, as for a type
    moved to another namespace or assembly;
  - a handler takes an interface, an abstract class, any object or a generic type.

  Every other unresolvable event is skipped with a warning, once per host, aggregate type and event
  type. The warning names both and says how to register or upcast the type.
- A host that replaces how recorded events are mapped keeps reading every event.
- Discovery of aggregates trusts the payload type of a handler that takes the enveloped event. It
  used to register the envelope type, which no recorded event names.
- The tenant package's documentation stops calling the cascade event something rehydration "skips
  silently" as though that already held. It says what does hold. The snapshot guide's paragraph on
  retiring an event becomes true as written.

A host whose streams contain only events that are registered, or only events its aggregates handle,
sees no difference except that unhandled events are no longer decrypted. No public signature
changes.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `aggregate-rehydration`: two requirements change.
  - *An unhandled event is skipped rather than rejected* gains what "skipped" has to mean for the
    promise to hold. The event's type is not resolved and its payload is not decrypted, so neither its
    registration nor its key decides whether the stream can be rebuilt. It also states the boundary:
    an event the aggregate might apply is never skipped for being unreadable, and an unresolvable skip
    is logged.
  - *Events are dispatched to the aggregate by their own type* gains that discovery trusts the payload
    of an enveloped handler.

## Impact

- `src/Stratara.Infrastructure/EventSourcing/AggregationService.cs` and
  `src/Stratara.Infrastructure/EventSourcing/SnapshotService.cs`: both select the entries an
  aggregate reads before mapping them.
- `src/Stratara.Infrastructure/EventSourcing/`: a new internal component that knows, per aggregate
  type, which recorded events the aggregate reads. It is registered by `AddEventSourcing()`.
- `src/Stratara.Abstractions/Abstractions/Reflections/TrustedTypeResolverServiceCollectionExtensions.cs`:
  discovery unwraps `IEvent<TEvent>`. `src/Stratara.Diagnostics/LogEvents.cs`: one new event id.
- `tests/Stratara.Infrastructure.Tests`: the selection rules, and store-backed rebuilds over a stream
  that holds an unregistered unhandled event, an event whose key is gone, an upcast event and a moved
  handled type, with and without a snapshot.
- `src/Stratara.Domain/README.md`, `docs/guides/configure-snapshots.md`,
  `docs/guides/tenant-membership.md`.
- `CHANGELOG.md`. A patch release: a bug fix with no new public surface.
- Nothing is dissolved or superseded by this change.
