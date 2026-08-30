## Context

See `proposal.md` — *Why*. The resolution order is implemented in `EventSource.ResolveSubjectAsync`,
and one line carries the whole defect:

```csharp
if (typeof(ITenantAggregate).IsAssignableFrom(typeof(TAggregate)))
{
    var existingTenantId = await LookupExistingAggregateTenantIdAsync(streamId, cancellationToken);
    ...
}
```

Two facts shape the approach:

- **`ITenantAggregate` adds one member.** `Guid TenantId { get; set; }`, hydrated from the creation
  event when events apply. Its only consumer in the framework is
  `AggregateOwnedByTenantAsync<TAggregate>`, whose generic constraint needs the tenant *on the
  object* to compare it against the session. It says nothing about storage.
- **Every entry records a subject regardless.** `EventStreamEntry.TenantId` is populated for every
  append, and `EventMapperFactory` decrypts each event with the subject recorded on its own entry.
  Ownership at the storage level is already universal; only the lookup is conditional.

The specification already contains the contradiction: its stated reason — stopping a privileged
session from re-homing an aggregate — does not depend on the aggregate's shape, while the requirement
it justifies does.

## Goals / Non-Goals

**Goals:**

- A stream's recorded owner is stable for every aggregate type.
- Shared ownership remains possible, but only when a consumer states it.

**Non-Goals:**

- Changing `ITenantAggregate`. It keeps its member and its purpose; this change stops *misusing* it,
  it does not redefine it.
- Repairing streams that already carry mixed ownership. See *Risks*.
- Any change to the explicit override, the batch cache, or the failure path.

## Decisions

### Decision 1 — Delete the condition rather than widen it

**Chosen:** remove the `ITenantAggregate` test so the stream lookup runs for every aggregate.

**Alternative considered — keep the condition and add a second marker interface** (say
`IStreamOwnedAggregate`) for aggregates that want a stable owner. Rejected: it makes correct
behaviour opt-in, and the failure mode of forgetting it is a silent data-ownership split discovered
at erasure time. It also repeats the original mistake — inferring a storage rule from a class's
declared interfaces.

### Decision 2 — Do not extend the explicit-override path

`AppendOnBehalfOfAsync` already outranks everything and is unchanged. It is what a consumer uses
when an event genuinely belongs to a different subject than the stream. No new API is needed: the
capability this change removes by accident is fully available by intent.

### Decision 3 — Accept the extra lookup, and measure where it lands

The lookup now runs for aggregates that previously skipped it. It is bounded by the existing
per-batch `_streamSubjects` cache, so it happens at most once per stream per batch, and only when
the stream already exists. This is the same read that `AR-2` and `UC-1` are separately trying to
reduce on the write path, so the cost should be stated rather than assumed negligible.

**Measured, 2026-08-30.** The figure is a round-trip count rather than a duration: a timing taken
against test doubles measures the doubles, and one taken against a real database measures that
database rather than this change. Counts are what a later change on the same write path can be
weighed against. Pinned by
`EventSourceTests.TheStreamOwnerLookup_*` so the numbers below fail a build if they move:

| Shape | Before | After |
|---|---|---|
| Append to an existing stream, aggregate is not `ITenantAggregate` | 0 reads | **1 transaction + `StreamExistsAsync` + `GetFirstOrDefaultAsync` = 2 queries**, once per stream per batch |
| Three events on that same stream in one batch | 0 | still **1 lookup** — the batch cache absorbs the second and third |
| Three streams × two events in one batch | 0 | **1 lookup per stream**, three in total |
| First append to a stream that does not exist yet | 0 | **1 query** — `StreamExistsAsync` returns false and the second read is never issued |
| Aggregate is `ITenantAggregate` | 1 lookup | unchanged |

So the worst case added by this change is **two queries per distinct existing stream per
`SaveChanges`**, and the common case of several events on one stream costs the same two as a single
event. It does not scale with the number of events.

## Risks / Trade-offs

- **Streams that already carry mixed ownership are not repaired.** After this change their existing
  entries keep the owners they were written with; only new appends follow the stream's first owner.
  A consumer with such a stream still has an erasure that does not cover all of it. Detecting them
  is possible — entries on one stream with differing `TenantId` — but repairing them means rewriting
  recorded events, which the store forbids by design. The honest position is to say so in the
  migration note rather than imply the change is retroactive.
- **It is breaking, and the break is invisible at compile time.** Nothing stops compiling; events
  simply start being attributed differently. A consumer deliberately exploiting the old behaviour
  gets no warning. This is why it belongs in a major version with a migration note, not in a minor.
- **A write-path read is added.** Bounded by the batch cache, but real. The tasks call for measuring
  it rather than asserting it is free.
- **`Tenant`, the framework's own aggregate, is affected.** It is a plain `IAggregate`, so its
  streams currently take the session's tenant on non-creation events. After this change they take
  the owner recorded at creation. That is the intended outcome, and it is worth calling out because
  it means the framework's own behaviour changes, not only a consumer's.
