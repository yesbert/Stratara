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

- Rebuilding an aggregate skips an event the aggregate declares no handler for **without reading
  its payload**. It does not resolve the event's type, does not decrypt it, and does not require the
  type to be registered. The decision is made on the event's recorded type name, after upcasting, so
  an event upcast into a type the aggregate handles is still applied.
- This holds for every rebuild: loading an aggregate, with or without a snapshot, and the rebuild
  that takes a snapshot while events are being saved.
- An event the aggregate **might** handle is never skipped because it cannot be read; it fails as it
  does today. That covers three cases:
  - The recorded name matches a handled type's name but does not resolve, for example because the
    type moved to another assembly.
  - The aggregate has a handler for a type other types can derive from.
  - The aggregate's handled types are not all registered, because the aggregate was registered by a
    route other than discovery.

  In all three, rebuilding reads every event exactly as it does today.
- An event the aggregate handles is read, resolved and applied as before. Nothing about registration
  changes. An unregistered type still fails on every other read path: projections, sagas, the bus.
- The tenant package's documentation stops calling the cascade event something rehydration "skips
  silently" as though that already held. It says what does hold: a consumer's aggregate needs no
  handler and no registration for it. The snapshot guide's paragraph on retiring an event becomes
  true as written.

A host whose streams contain only events that are registered, or only events its aggregates handle,
sees no difference except that unhandled events are no longer decrypted. No public signature
changes.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `aggregate-rehydration`: *An unhandled event is skipped rather than rejected* gains what "skipped"
  has to mean for the promise to hold. The event is not read, so neither its registration nor its key
  decides whether the stream can be rebuilt. It also states the boundary: an event the aggregate
  might apply is never skipped for being unreadable.

## Impact

- `src/Stratara.Infrastructure/EventSourcing/AggregationService.cs` and
  `src/Stratara.Infrastructure/EventSourcing/SnapshotService.cs`: both select the entries an
  aggregate reads before mapping them.
- `src/Stratara.Infrastructure/EventSourcing/`: a new internal component that knows, per aggregate
  type, which recorded events the aggregate reads. It is registered by `AddEventSourcing()`.
- `tests/Stratara.Infrastructure.Tests`, `tests/Stratara.Testing.EntityFrameworkCore.Tests`: the
  selection rules, and store-backed rebuilds over a stream that holds an unregistered, unhandled
  event, with and without a snapshot.
- `src/Stratara.Domain/README.md`, `docs/guides/configure-snapshots.md`,
  `docs/guides/tenant-membership.md`.
- `CHANGELOG.md`. A patch release: a bug fix with no new public surface.
- Nothing is dissolved or superseded by this change.
