## Context

See `proposal.md` — Why. The owner's standing decision: the framework's own products are all
PostgreSQL with the native reader; the portable reader is *secured* (`make-the-portable-reader-fail-loudly`,
#106), not extended. This change stays inside that: it changes no reader capability and adds no
provider; it makes four sentences of the specification true. The current code:

- **Backfill.** `PartitionCounterBackfill.RunPartitionAsync` (`src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PartitionCounterBackfill.cs:47-90`)
  locks the partition's counter row, lists the unpositioned entries in sequence order, shifts every
  positioned entry of the partition by their count (lines 63-70), numbers the unpositioned ones from 1
  (lines 72-83), and sets the counter to `counter + shift` (lines 85-87). Its summary says "entries
  already positioned keep their order and move up behind them"; its remarks say a checkpoint written
  before it "is no longer meaningful afterwards and must be reset" (lines 17-19). The reader resumes
  after its checkpoint (`StoreReaderLoop.cs:186-196`), and the loop caches the position it last
  wrote. `PartitionCounterBackfillTests` (`tests/Stratara.Orleans.IntegrationTests/CommitOrder/PartitionCounterBackfillTests.cs:39-68`)
  appends eight positioned entries *before* the backfill and asserts history first, live after — the
  shift is what the test pins. `PortableReaderFailsLoudlyTests.A_read_stops_at_an_entry_appended_without_a_position_until_it_is_positioned`
  reads from 0 after the backfill (`:47`), so no test holds a checkpoint across one.
- **Probe.** `PortableCounterReader.RefuseUnpositionedAsync` (`PortableCounterReader.cs:82-96`)
  selects up to `UnpositionedProbe = 64` rows with a null position, any partition, no order, and
  filters by partition in memory; the comment says the bucket filter "cannot use" the position index.
  `PortableCounterStartupCheck` (`PortableCounterStartupCheck.cs:29-35`) uses `AnyAsync` on the null
  position without a partition filter — the start-up refusal is complete; the per-read refusal is not.
- **Interceptor.** `PartitionCounterInterceptor.SavingChangesAsync` (`PartitionCounterInterceptor.cs:45-63`)
  takes `ChangeTracker.Entries<EventStreamEntry>()` in enumeration order, groups by partition, and
  `StampPositionsAsync` (lines 135-161) hands positions out in that order. The sequence number is
  database-generated (`EventStreamEntryConfiguration.cs:23`), so it is unknown when stamping; the
  entry's `Id` is a version-7 GUID (`EventSource.cs:235`), ordered by millisecond and random within it.
  The stream id and the version are known.
- **Switch.** `CommitOrderOptions.MaintainPartitionCounter` (`src/Stratara.Orleans/CommitOrder/CommitOrderOptions.cs:22-32`),
  default `true`, remarks "the framework does not read this value". The only readers are test
  contexts (`tests/Stratara.Orleans.Scenarios/Store/PocCommitOrderWriteDbContext.cs:21-24`) and the
  ~25 test configurations that set it. The framework's write-context factory
  (`NpgsqlDbContextServiceCollectionExtensions.cs:131-136`) adds no interceptor from the container.
  `Directory.Build.props` says `4.1.1`; the option shipped in 4.1.0.

## Goals / Non-Goals

**Goals:**
- Every scenario of the requirement is true of the code: a checkpoint survives a backfill; the reader
  stops at an unpositioned entry of its partition in every case; positions within an append follow
  version order; no setting claims to switch the counter.
- No change to the reader's ordering guarantee, its name, its schema or its start-up checks.

**Non-Goals:**
- Wiring `MaintainPartitionCounter` so the framework adds the interceptor to a consumer's write
  context (D4).
- Renumbering positions in any case, or migrating checkpoints across a backfill.
- Detecting, in the backfill, a stream whose order the positioning cannot keep (open question).
- Any provider beyond PostgreSQL for verification; the scenarios say so.

## Decisions

### D1 — The backfill appends after the counter and never renumbers

`RunPartitionAsync` keeps its transaction and the counter lock, lists the unpositioned entries of the
partition in sequence order as today, and hands them the positions `counter + 1 … counter + n`, in
that order; the counter becomes `counter + n`; no positioned entry is touched. On a store adopted as
documented — positioned before any host with the counter appends — the counter is 0 and the result is
today's: history in sequence order from 1. On a store where a counted host appended before the
backfill, the late entries follow those appends. The class summary and remarks change with it: the
sentence that a checkpoint "must be reset" goes, replaced by the window — stop the process that
appended without the counter before positioning, because an entry it appends after a counted append
to the same stream is positioned after that later version, which a version-checking read model
reports as a missing preceding fact until the projection is rebuilt.

Why this and not the shift: a checkpoint is a promise that everything at or below it was applied.
Inserting below a checkpoint breaks the promise for every reader past the insertion point, and the
framework has no way to reach those checkpoints — they are in a read store the backfill does not see,
under names of consumers it does not know. Appending keeps every promise; what it costs is the order
across the adoption boundary, which the documentation states and a rebuild repairs. The precedent is
the transaction-id backfill (`adopt-the-execution-model-on-a-populated-store` D3), which accepts the
same inversion for the rows of its window and names the window.

*Rejected: shift with a generation in the reader's name, refusing every older checkpoint.* Turns every
positioning of a late entry into a full rebuild of every consumer; the specification says the reader
resumes.
*Rejected: inserting the late entry at its sequence place and shifting only what follows.* Still
breaks every checkpoint past the insertion point, silently — today's defect in a smaller radius.
*Rejected: leaving the shift and changing the specification to "reset after every backfill".*
A reset while nothing runs, for every foreign append, is not an operation a team can run on a live
system; the loud stop of #106 would become a stop until downtime.

Evidence: `PartitionCounterBackfill.cs:47-90`, `StoreReaderLoop.cs:186-196`, `PartitionCounterBackfillTests.cs:39-68`
(the assertion that flips), `PortableReaderFailsLoudlyTests.cs:47`. Test: positioned 1..10 with a
checkpoint at 10; a foreign append; the backfill; a read after 10 returns exactly the foreign entry at
11 and the positions 1..10 are unchanged — against today's code the read returns the old entry 10 and
never the foreign one.

### D2 — The probe is one ordered query per partition

`RefuseUnpositionedAsync` becomes
`Where(position == null && BucketId % count == partition).OrderBy(SequenceNumber).Select(SequenceNumber).FirstOrDefaultAsync()`.
The null-position predicate is what the position index serves — an unpositioned entry is the anomaly,
so the rows it yields are few — and the partition predicate is applied to those rows by the database
rather than in memory after a cut. There is no limit to cut, and the entry named is the earliest of
the partition. `HeadAsync` shares the probe and gains the same.

*Rejected: raising the probe to a larger constant.* Any constant is a number of foreign appends that
hides the entry.
*Rejected: dropping the per-read probe and relying on the start-up check.* The start-up check sees
only what existed at start; the per-read probe is what stops a partition at an entry appended while
the host runs.

Evidence: `PortableCounterReader.cs:29-30,82-96`, `PortableCounterStartupCheck.cs:29-35`. Test: one
foreign append to the reader's partition, then more foreign appends to other partitions than one read
returns (the batch size plus one, at least sixty-five); the read of the partition is refused naming its
own entry.

### D3 — Positions within an append follow the stream's version order

`SavingChangesAsync` orders the added entries before grouping: by the order in which their stream
first appears in the tracker, then by version. Within a stream the guarantee is version order, by
contract; across streams the order the entries were added is kept where the tracker keeps it, which
is what a bundle carried on the bus. `StampPositionsAsync` is unchanged.

*Rejected: ordering by the entry's id.* A version-7 GUID orders by millisecond and is random within
one, so two versions of a stream created in the same millisecond may invert.
*Rejected: ordering by sequence number.* Generated by the database on insert; unknown when stamping.

Evidence: `PartitionCounterInterceptor.cs:45-63,135-161`, `EventStreamEntryConfiguration.cs:23`,
`EventSource.cs:233-241`. Test: one save with versions 3, 1, 2 of a stream added in that order, and a
second stream; the positions read back follow 1, 2, 3 within the stream.

### D4 — The switch is obsolete, not wired

`MaintainPartitionCounter` gets `[Obsolete("The framework does not read this value. A write context that maintains the partition counter adds PartitionCounterInterceptor to its interceptors; see the migration guide.")]`
and stays otherwise; its remarks already say what the message says. It is removed with the next
major, listed with the deprecated members. The test contexts that read it — `PocCommitOrderWriteDbContext`
and the configurations that set it — move to a switch of the test store's own, since the repository
builds with warnings as errors.

Why not wire it: a wire means the framework's write-context factory adds `PartitionCounterInterceptor`
to the consumer's context when the value is `true`. That factory lives in
`Stratara.EventSourcing.EntityFrameworkCore`, which cannot reference the interceptor's package without
a cycle, so the wire would be a general "interceptors from the container" mechanism on the store
registration plus a registration for appenders — an extension of the portable track, which the owner
has deferred. A switch that switches nothing is worse than no switch; the honest minimum is to say so
at compile time.

*Rejected: removing the property now.* A compile break in a patch, for a member that shipped in
4.1.0.
*Rejected: reading the value in `AddStrataraPortableCounterReader` to fail the host when it is
`false`.* The value's meaning was "this process maintains the counter"; the reader's host is not
necessarily an appender.

Evidence: `CommitOrderOptions.cs:22-32`, `PocCommitOrderWriteDbContext.cs:21-24`,
`NpgsqlDbContextServiceCollectionExtensions.cs:131-136`, `docs/guides/migrate-to-the-orleans-execution-model.md:156-159`,
`CHANGELOG.md:64`. Test: the surface test lists the member as obsolete; the documentation test
compiles the guide's snippets without it.

## Risks / Trade-offs

- [A team that backfilled with the shift and reset its checkpoints, as the old remark said] → nothing
  changes for them; positions already handed out are never touched, and a reset store starts at 0.
- [A late entry positioned after a later version of its stream] → named in the guide with the window
  and the remedy (rebuild the projection that stopped); the loud stop of #106 is what makes the window
  visible.
- [`PartitionCounterBackfillTests` pins the old order] → its expectation flips to positioned-first,
  late-after; the test's comment says why.
- [The per-partition probe on a provider whose planner does not use the index for `IS NULL`] → the
  scenarios say PostgreSQL only; the rows are the anomaly, and the start-up check already runs the
  same predicate.
- [The obsolete warning breaks a consumer with warnings as errors] → the message says what to change;
  the property is a boolean nobody needs to set.

## Migration Plan

Patch release. No new public surface; one member obsolete. A team on the portable reader that runs a
backfill after this release sees positioned entries left in place and late entries after them; the
guide's sentence about resetting checkpoints after a backfill is removed. Rollback: a store
positioned by the new backfill is a valid store for the old reader, whose reads do not depend on how
positions were assigned.

## Open Questions

- Should the backfill refuse — rather than position — an entry whose stream already holds a positioned
  later version, naming the stream? Refusing leaves the partition stopped until an operator acts by
  hand; positioning lets the version-checking read model stop with a preceding-fact failure that a
  rebuild resolves. This proposal positions and documents; the owner may prefer the refusal.
