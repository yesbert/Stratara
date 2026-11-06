# Design

## Context

See `proposal.md` for the motivation. Three facts from the code shape the approach.

- `ProjectionHandler.HandleForgettingAsync` asks `IForgottenTenantStore` only after a projection threw
  `PrecedingFactMissingException`. A failing store is rethrown as that same exception with the store's failure inside,
  so the bundle or batch keeps its retry and ends as any missing prerequisite does.
- The saga consumers' checkpoints (`sagas:<Name>`, and `sagas` as the shared row of releases before 4.2.0) sit in one
  table with the projections'. `IProjectionCheckpointStore` addresses a checkpoint by consumer, partition and reader
  name. It cannot enumerate. Saga readers are registered only by `AddStrataraSagaGrains`, so a host with projections
  alone does not know the sagas' names, and placing the roles on different silos is a documented deployment.
- `ReplayCheckpointResetTruncator` already wraps the host's truncator, between a pause of the projection readers and
  two resets of their checkpoints. Store readers, saga readers included, hold back while `IsReplayActive`, which the
  replaying host sees at once and another host within its refresh period (`StoreReaderGrain.Suspended`).

## Goals / Non-Goals

**Goals:**

- A row-adding fact of a recorded tenant leaves no row, whatever order the readers meet it in.
- Saga positions survive a truncator that removes them, on every host where the replay wraps the truncator.
- No breaking change to the public API, and no write to a saga position that did not need one.

**Non-Goals:**

- Removing the second application of the store in a full replay. It is what makes the first problem routine, but it
  is the existing refill of store-reading projections, and this change makes it harmless for these rows.
- Pausing saga readers during a replay.
- Guarding a rebuild of one projection, which empties that projection's own read model and does not run the truncator.
- A deleted tenant's facts on a projection that does not declare the interface.

## Decisions

### 1. Consult the record before handling, for every fact but two kinds

`HandleForgettingAsync` checks first. It skips the check for `TenantDeleted` and `CustomerTenantsDeleted`, and for a
fact whose `TenantId` is `Guid.Empty`. A recorded tenant's fact is logged through the existing
`LogProjectionForgottenTenantFactPassedOver` (`104_014`) and returned from, without calling the projection. The catch
for a report stays: a deletion can be recorded by another partition between the check and the handler, and that
projection's report must still be passed over.

- **Deletion facts are exempt** because applying one again must remove what is there. A full replay on a store-reading
  host applies each deletion twice, and an envelope tenant may already be recorded.
- **An empty tenant is not consulted** because no tenant owns the fact, so the read could only answer no. A system fact
  costs nothing and cannot be dropped by a stray record.
- **Alternative rejected: ask the store inside each projection.** It works, and is what the reporting consumer did for
  two projections, but it leaves every other declaring projection exposed. The declaration already promises the data
  is gone.
- **Evidence:** `ProjectionHandler.cs`, the reported measurements, and the tests in tasks 1.1 to 1.4.

### 2. No cache: one read per fact and declaring projection

Each relevant fact costs one `HasForgottenAsync`, which on the framework's store opens a context and queries the
primary key `(Projection, TenantId)`. `ProjectionManager` and `ProjectionGrain` hand over relevant facts only, so a
projection pays for the facts it handles and for nothing else.

Measured roughly on 2026-11-06: 5000 facts through the default handler with a no-op handler, the framework's store on
PostgreSQL in a local Docker container, three rounds, with a one-minute load between 4 and 6. An undeclared projection
took 1.4 to 4.2 ms in total; a declaring one took 1660 to 2805 ms, so **about 330 to 560 µs per fact**. At that rate a
million relevant facts cost about six to nine minutes more per declaring projection, on a store this close. A store
across a network costs its round trip instead. It was a throwaway test, not a benchmark, and is not in the repository.

A cache has to meet both conditions in the brief, and none examined does:

| Cached answer | Why it fails |
|---|---|
| "Not forgotten", any lifetime | A deletion applied by another partition or host is hidden for that lifetime. The race named first under Risks grows from one read's distance to the cache's lifetime. |
| "Forgotten", across batches | `ClearAsync` runs from the replay worker or a rebuild, possibly on another host, with no signal to this one. The answer outlives the record, and facts are dropped in the rebuild that follows. |
| Either, for one `ProjectAsync` call | Negative answers still widen the race to the batch's duration. Invalidating on the handler's own deletions does not see another partition's. |

The positive answer is the only one that is safe to keep for one call. It would help only a batch dominated by one
deleted tenant, which is not the common case and does not justify a second code path. If measurement later shows a
need, the store is the place to optimise, for example a batched lookup, and that can be done without changing specs.

- **Evidence:** `ForgottenTenantConfiguration.cs` (the key), `ProjectionManager.cs:27-42`, `ProjectionGrain.cs:189-199`;
  a test pins one read per fact (task 1.3).

### 3. A record that cannot be consulted is a missing prerequisite

A failure of the check before handling becomes
`new PrecedingFactMissingException(@event.StreamId, @event.EventTypeName, storeFailure)`, and the projection is not
called. That follows the existing treatment, so one path decides: retry, then dead letter live, failed batch in a
replay, and a stopped partition on a reader. Applying would risk the row this change exists to prevent, and dropping
would lose a legitimate fact. `OperationCanceledException` passes untouched, as it does today. `HasForgottenAsync`
takes the stream and type name as parameters so that both call sites share it.

- **Evidence:** `A_store_that_fails_is_reported_as_the_missing_prerequisite` already covers the shape; task 1.4 adds
  the assertion that the projection was not called.

### 4. A fact that removes something elsewhere is passed over too

The decision uses the envelope's tenant and cannot know what a handler would do. A fact owned by a recorded tenant
whose handler removes another tenant's row is passed over, and the row stays. This is a limit and is documented on
`IForgetsDeletedTenants` and in the guide: such a projection records that fact under the tenant whose data it changes,
or does not declare the interface. The same applies to a fact recorded under a deleted tenant's envelope about another
tenant's data generally.

### 5. Guard: list the positions, compare after the truncation, write only what is lost

The guard runs in `ReplayCheckpointResetTruncator`:

1. After the pause and **before the first reset**, it lists the positions. A failure here propagates with nothing
   changed. Inside the truncate step would be too late, because the first reset has already returned the projections'
   checkpoints to the beginning over full read models. The hold's disposal resumes the readers.
2. Around `inner.TruncateAllAsync`, it captures any failure and lists again. Comparing the two lists costs two reads
   and no writes on a truncator that behaves.
3. For each remembered row, by consumer and partition:
   - none found: `CreateAsync`, which never replaces a row;
   - found under the same reader at a lower position: `SetAsync` to the remembered position;
   - found further on, or at the same position: left alone;
   - found under another reader: left alone, because positions are not comparable across readers.
4. It logs the warning if anything was written, naming the truncator's type, the count and the consumers.

The write-back and second list use `CancellationToken.None`, as the existing repair reset does: a cancellation during
the truncation must not leave the positions lost. A failure of the write-back logs an error with what was lost and
fails the replay. Together with a truncator failure it becomes an `AggregateException`; otherwise the truncator's own
failure stands. The projections' resets of `TruncationBetweenResets` are unchanged, and they touch no `sagas` row.

**Lower position counts as lost.** `AdvanceAsync` on a missing row makes a running reader read again, find `0` and
insert, or a starting reader's `SagaStart` creates a row at the beginning. Checking only for absence would then leave
the saga at the beginning, which is the failure being guarded against. Saga positions only move forward outside a
reset, and no reset of a saga's checkpoint runs inside a replay.

- **Alternative rejected: use `FindAsync` for known sagas.** It needs no new member, but it misses every saga on a
  projections-only replaying host, needs the reader name and partition count to match, and throws on a row written
  under another reader.
- **Alternative rejected: refuse a truncator that removes positions.** The damage has been done by then, and a replay
  that fails leaves the read models empty.
- **Evidence:** `ReplayCheckpointReset.cs`, `StoreReaderLoop.cs` (`WriteAsync`, the re-read), `SagaReaderGrain.cs`
  (`SagaStart`), `ProjectionCheckpointStore.cs` (`AdvanceAsync`, `CreateAsync`); tests in group 3.

### 6. All saga rows the store can list, through an optional member

This answers the question in the brief: **the guard covers every saga row in the store**, not only the sagas the
replaying host registers. It is not breaking, so the fallback to the contract correction alone is not needed.

```csharp
Task<IReadOnlyList<StoredCheckpoint>> ListAsync(string consumerPrefix, CancellationToken cancellationToken = default) =>
    throw new NotSupportedException(...);

public sealed record StoredCheckpoint(string Consumer, int Partition, string Reader, long Position);
```

This follows the pattern of `FindAsync` and `CreateAsync`, which were added the same way. Nothing an implementer
compiled against changes. Adding a default interface member is additive, so a patch by this project's versioning
rule. The framework's `ProjectionCheckpointStore<TContext>` overrides it.

The guard asks for the prefix `sagas` and keeps `sagas` exactly and anything starting `sagas:`, comparing ordinally on
the client. Provider-specific case rules in `LIKE`, and a projection named `sagasFoo`, therefore cannot match. The
shared row is included because `SagaStart` still reads it for a saga without a checkpoint. `ConsumerPrefix` and
`ConsumerName` stay internal; the contract spells out the prefix for a consumer.

A store without the member throws `NotSupportedException`. The replay then proceeds, logging that the positions were
not guarded and naming the store: a replay that used to work must not start failing, which would be breaking in
effect.

- **Alternative rejected: `IProjectionCheckpointStore` unchanged and a new optional interface.** It makes one more thing
  to discover and register, for the same result.

### 7. Three log events, in the Orleans bucket

`LogEvents.Orleans`, next free after `117_137`, source-generated in `OrleansLog.cs`, listed in
`docs/reference/log-events-schema.md`:

| Event | Level | Meaning |
|---|---|---|
| `ReplaySagaCheckpointsRestored` (`117_138`) | Warning | The truncator named removed or moved back these positions; they were written back. |
| `ReplaySagaCheckpointsNotGuarded` (`117_139`) | Warning | The store named cannot list; a truncator could remove the positions unseen. |
| `ReplaySagaCheckpointsNotRestored` (`117_140`) | Error | These positions could not be written back, with their values; an operator must restore them. |

The truncator's constructor gains a trailing optional `ILogger<ReplayCheckpointResetTruncator>?`, so the existing test
construction stays valid. The type is internal, so this touches no public API.

### 8. Contract text

The `IProjectionViewTruncator` summary drops the `TRUNCATE ... CASCADE` recommendation and states: a truncator empties
read models; the checkpoint rows of the saga consumers, whose names start `sagas:`, must survive, because they share
the read store and a saga without its position runs its side effects again; and a cascade across the schema takes them
with it. It also says the Orleans host writes back what it finds removed and warns, as a guard rather than a licence.
`docs/guides/write-a-projection.md` says the same where the replay is described.

### 9. The requirement is restated, and its rationale carried here

The name of the scenario "A late fact of a deleted tenant is applied without complaint" cannot be kept for the opposite
behaviour, and a `MODIFIED` block may not drop a scenario. The spec delta therefore removes *A projection can forget a
deleted tenant* and adds six requirements, each within the length strict validation allows, with every other scenario
unchanged. The rationale of the removed requirement does not fit that length, so it stands here, verbatim:

> A deletion removes a tenant's data from a projection on purpose. A fact recorded for that tenant
> afterwards is legitimate history: work queued before the deletion ran to its end. The projection
> meets it with nothing to apply it to. The missing-prerequisite report can tell "not yet" from "never"
> only by waiting, so it turns such a fact into a dead-lettered bundle live and into a failed replay on
> rebuild. A replay that fails leaves the read models it emptied empty. Knowing which tenants the
> projection itself has deleted is what separates "removed on purpose" from both.
>
> The knowledge is kept per projection because projections apply facts in parallel, and on a
> store-reading host each at its own pace. A shared record could tell one projection about a deletion
> it has not applied yet. A record is emptied before the read model rather than after it for two
> reasons. A deletion applied in between is then recorded again rather than lost, while a record that
> outlives its reset only concerns a tenant whose data is removed at the deletion in any case. And a
> missing place to keep it then fails the replay before anything has been emptied.

What this change adds to it: a fact that adds a row reports nothing, so a record consulted only after a report would leave
that row behind for good, because the deletion that should have removed it has already been applied. Readers partition by
stream, so a tenant's deletion can be read before a fact of that tenant on another stream, and a full replay on a
store-reading host applies the store twice, which makes that order routine. The record is therefore consulted before
every fact, and the deletion facts are exempt because applying one again must still remove what is there.

## Risks / Trade-offs

- **[A deleted tenant's fact can still land]** The check and the handler are not one transaction: a deletion applied by
  another partition between them leaves the row, if the row's write lands after the deletion's removal. → The window is
  one read wide. Closing it needs the projection's own transaction, which the framework does not hold. Documented.
- **[Replay gets slower on opted-in hosts]** One more round trip per relevant fact and declaring projection, measured in decision 2.
  → Only declaring projections pay. Decision 2 names where to optimise if it matters.
- **[A consumer relied on "applied as usual"]** A declaring projection that deliberately applies a deleted tenant's
  facts now stops. → It already broke the declaration's promise. Stated first in the proposal and in the guide, and
  the release notes should carry it.
- **[A saga moves during the guard]** A reader on another host that has not yet learned of the replay can advance after
  the first list; the write-back then restores an older position, its next advance is refused, and it re-reads that many
  entries. A running reader can also re-read from the beginning for one batch if its write met the missing row. → At
  worst the entries of a batch or two run again, which is what the sagas' at-least-once delivery already allows, against
  the whole history without a guard. Bounded by the replay state's refresh period.
- **[A row is created between the second list and the write]** `CreateAsync` returns `false` and that row stays where it
  was created. → A window of one round trip, on a reader that has not learned of the replay. The warning still reports
  the truncator's effect.
- **[Only where the decorator is]** The guard exists only on a host that calls `AddStrataraProjectionGrains`. A host with
  saga grains and bus-fed projections replays with an undecorated truncator and has only the contract. → Documented as a
  limit. Decorating from `AddStrataraSagaGrains` as well would add a second registration order rule for a combination
  nobody has reported.
- **[A custom store is not guarded]** → The warning names it, and the XML documentation says how to enable the guard.
- **[A failed replay with `AddReadModelRestore`]** The copy includes the checkpoint table, so a write-back returns the
  saga rows to their values when the replay started. Saga readers are held throughout, so these are the same values. No
  change needed; `IReadModelPreservation` is untouched.

## Migration Plan

None required: no schema change, and existing stores and truncators keep working. A consumer whose truncator empties the
checkpoint table sees the new warning at its next replay and should correct the truncator. A consumer with a hand-written
guard in a declaring projection can remove it. Rollback is a code revert, with no data to convert.
