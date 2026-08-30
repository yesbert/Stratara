# Design — Reject an ownerless event subject

## Context

See `proposal.md` — *Why*. Two mechanically independent defects that share one consequence: an event
lands with a subject nobody chose.

The relevant shape today: `EventSource.ResolveSubjectAsync` walks five candidates in priority order.
Candidates three through five each carry an `!= Guid.Empty` test and the walk ends in a throw naming
the event, the stream and the three ways to supply a subject. Candidate one — the per-event override
that `AppendOnBehalfOfAsync` deposits in `_explicitSubjectOverrides` — returns straight out of the
dictionary. `AppendOnBehalfOfAsync` is the only writer of that dictionary.

`Stratara.Domain` already references `Stratara.Abstractions` (the tenant aggregate implements
`IAggregate` from it), so `TenantCreated` can implement `IAggregateCreationEvent` without a new
package reference and without crossing a tier.

Evidence both defects rest on: the source as it stands at `24254be0`, plus the two reproductions
the consumer that found them recorded in its framework-findings report.

## Goals / Non-Goals

**Goals:**

- An append can no longer record an entry whose subject names no tenant, by any route.
- Creating a tenant produces the same owner whatever session performs it.
- Both are covered by tests written before the fix.

**Non-Goals:**

- Changing the priority order, or what any of candidates two through five do.
- Making the tenant aggregate tenant-scoped over itself so that candidate three applies to its later
  events. That is `anchor-event-subject-to-the-stream`'s question and it is being worked there;
  touching the same condition from two changes would collide.
- Repairing streams already written with an empty subject. Nothing here reads or rewrites stored
  entries.

## Decisions

### The empty-subject check goes on `AppendOnBehalfOfAsync`, not on candidate one

`AppendOnBehalfOfAsync` validates the subject before it registers the override, so a bad subject
never enters `_explicitSubjectOverrides` and no event is buffered. Putting the check in
`ResolveSubjectAsync` instead would work, but it fires one layer down: the override is already
registered, the entry is already being built, and the stack the consumer sees points into resolution
rather than at their own call.

The single check on the entry point also keeps candidate one honest by construction — the dictionary
cannot hold an empty subject, so the resolution walk needs no defence against one. A second guard
inside `ResolveSubjectAsync` would be unreachable, and unreachable guards are how the next reader
concludes the entry point does not check.

*Alternative rejected — fall through to the remaining candidates.* Mechanically the smallest change,
and it is what candidates three through five do to each other. It is wrong here because the other
candidates are things the framework *derives*; falling from one derivation to the next contradicts
nobody. Candidate one is a caller stating the owner, which is simultaneously a statement that the
session is not to be used. Silently using the session anyway would answer an explicit instruction
with the one thing it ruled out — and it would do so invisibly, which is the property that made this
defect survive.

### `ArgumentException`, not `InvalidOperationException`

The house idiom for a rejected caller-supplied value is `ArgumentException` with `nameof`
(`SettingCatalog`, `PermissionCatalog`, `EfApiKeyStore`). The subject arrives as a parameter and is
rejected on its own, before any state is consulted — that is an argument fault, not an invalid state.
The sibling failure at the end of the resolution walk stays `InvalidOperationException`: it is
reached only after the framework has consulted stream, event and session, and it reports that the
*situation* yields no tenant. The two are different failures and it is worth their types saying so.

The message names the event type and the stream id, matching what the end-of-walk failure already
gives a reader.

### `TenantCreated` implements the creation contract explicitly

`Guid IAggregateCreationEvent.TenantId => Id;` as an explicit interface implementation. Implicit
would add a public `TenantId` property to a record that already exposes the same value as `Id`, which
invites a consumer to wonder which one is authoritative and creates a second name for one field in
the published surface. Explicit keeps the record's public shape byte-identical.

The record's positional parameters, their order, their names and their JSON representation are
untouched, so serialized payloads already in a stream deserialize exactly as before and no migration
is needed. The interface is a compile-time contract read at append time; it is not serialized.

## Risks / Trade-offs

- **A consumer is appending on behalf of an empty subject today and their append starts throwing.** →
  That is the change, and it is called out as breaking in the proposal. The alternative is that they
  keep writing entries their own erasure will not reach. The exception names the stream and the
  event, so the offending call site is identifiable from the message alone.
- **New tenants change owner, so a consumer's operator-created tenants split into two eras.** →
  Only creations after adoption are affected; nothing rewrites existing entries. A consumer who was
  relying on operator ownership was relying on the accident this change removes, and the era boundary
  is the adoption version.
- **Overlap with `anchor-event-subject-to-the-stream`.** → Both changes touch subject resolution.
  This one adds a guard on the entry point and leaves the `ITenantAggregate` condition exactly as it
  is; ES-2 removes that condition and does not touch the entry point. Disjoint edits in the same
  method — whichever lands second rebases cleanly, but it should be reviewed rather than merged blind.

## Migration Plan

No data migration. Consumers adopt by version bump. A consumer that hits the new exception fixes the
call site that passes an empty subject — the exception message names the stream and the event.
Rollback is the previous package version; nothing written under this change is unreadable by it.
