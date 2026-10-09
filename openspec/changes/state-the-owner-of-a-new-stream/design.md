## Context

`EventSource.ResolveSubjectAsync` (`src/Stratara.Infrastructure/EventSourcing/EventSource.cs`) resolves an
unstated owner in the order: per-batch cache → owner recorded on the stream's first entry
(`LookupStreamOwnerAsync`) → `IAggregateCreationEvent.TenantId` → `SessionContext.TenantId` (with the
session's data-owner user) → failure. A stated subject never reaches it (`statedSubject ?? Resolve…`), and
`AppendEventToStreamAsync` records a stated subject as the stream's owner when it lands on version 1.
`AppendOnBehalfOfAsync` onto a stream that does not exist therefore already creates it with the stated
owner (`EventSourceTests.cs:630`) — but without the "stream exists" refusal that `CreateAsync` makes.

The session step is reached in two situations: the first event of a stream that does not exist, and an
append to an existing stream whose first entry recorded no tenant (`LookupStreamOwnerAsync` returns null
for both). Only the first concerns this change.

After `append-against-the-version-a-handler-read`, `AddEventSourcing()` binds `EventSourcingOptions` and
`EventSource` takes `IOptions<EventSourcingOptions>?`.

Evidence: the implementation at `main` 398d20d; NextPA finding F-005 (*Suggested fix*, first two bullets;
the third, documentation, is done in `docs/guides/write-a-command-handler.md` lines 70–117).

## Goals / Non-Goals

**Goals:**
- An explicit, discoverable way to state the owner at creation.
- A host can make "owner from the session on a new stream" visible, or impossible.
- No behaviour change for a host that does nothing.

**Non-Goals:**
- Making `Warn` or `Refuse` the default. A tenant user creating a record in their own tenant is the
  ordinary case, and for it the session *is* the right owner; a default warning would be noise on every
  such creation. A major version can revisit it.
- A per-aggregate or per-command policy. The host-wide switch is what a consumer's build-time ratchet
  approximates; finer grain can come later without changing this shape.
- `CreateRangeOnBehalfOfAsync`. Every later event in the batch already keeps the stated owner of the first;
  `CreateOnBehalfOfAsync` followed by `AppendRangeAsync` covers it.

## Decisions

**`CreateOnBehalfOfAsync` is `CreateRangeAsync`'s existence check plus the stated-subject path.** The
implementation checks existence on its own transaction (as `CreateRangeAsync` does), seeds the stream at
version 0, guards the empty tenant (as `AppendOnBehalfOfAsync` does), and stages through
`AddEventsToStreamAsync` with the stated subject — which already records it as the stream's owner on
version 1. The default interface implementation does the same in terms of public members: `ExistsAsync`
→ throw `InvalidOperationException`, else `AppendOnBehalfOfAsync`. It cannot see a stream staged but not
yet saved in the same batch; the framework's own implementation can, and overrides it.

**The policy applies at the session step, only for a new stream.** `ResolveSubjectAsync` gets the stream
version being written (`streamVersion == 1` means the stream has no entry before this one, in the store or
in the batch). At the session step: `Allow` returns the session owner; `Warn` logs `102_104` and returns
it; `Refuse` throws `InvalidOperationException`. The per-batch cache makes the step run at most once per
stream per batch, so `Warn` logs once per created stream, not once per event. An existing stream whose
first entry has no tenant keeps today's behaviour under every setting — that is legacy data, not a
creation.
- *Alternative:* apply the policy in `CreateAsync`/`CreateRangeAsync` only. Rejected: `AppendAsync` on a
  missing stream also creates it, and is how many handlers write their first event.

**`Refuse` throws before staging.** The subject is resolved before the entry is built and added, so a
refusal leaves `_eventStreamEntries`, `_streamVersions` and `_streamSubjects` as they were. One exception
to that is the version seeded by `CreateRangeAsync` (0) before the first event; it is harmless (the save
clears it, and a retry re-seeds it), but the test asserts nothing is staged.

**Log id `102_104`, warning.** `LogEvents.EventStore` uses `_1xx` for errors and has `102_101..102_103`
defined but unused; the next free number in that band is `102_104`. A warning sits there by the same
convention the messaging band follows (`108_107` is a warning). Message:
"Stream {StreamId} was created by {EventType} with its owner taken from the session: tenant {TenantId}.
State the owner with CreateOnBehalfOfAsync, AppendOnBehalfOfAsync or an IAggregateCreationEvent."

## Risks / Trade-offs

- [A host turns on `Refuse` and seeding code that runs under a platform session starts to fail] → That is
  the point of the switch; the message names the three ways to state an owner, and `Warn` exists to find
  every such site before refusing.
- [`EventSourcingOptions` gains a second member in a second change] → Both changes ship in the same release
  if the owner bundles them; if not, the second extends what the first introduced.

## Migration Plan

Additive. A consumer that wants the guard sets `EventSourcing:NewStreamOwnerFromSession` to `Warn` in a
lower environment, removes the sites it reports, then sets `Refuse`. Rollback is `Allow`.
