## Context

`EventSource` (`src/Stratara.Infrastructure/EventSourcing/EventSource.cs`) numbers an append from a
per-batch dictionary `_streamVersions`. The first append to a stream fills it lazily with
`GetVersionOrDefaultAsync` on its own transaction (`AppendRangeCoreAsync`), so the base is the head *at
the first append*, not at the handler's read. `CreateRangeAsync` sets it to 0 after an existence check.
`SaveChangesAsync` relies on the unique index `(BucketId, StreamId, Version)`
(`EventStreamEntryConfiguration.cs:24`) to turn a collision into `ConcurrencyException`; there is no
explicit version check anywhere, and no "expected version" concept in the code base
(`grep ExpectedVersion` finds nothing).

`AggregationService` and `EventSource` are separate scoped services that share no state.
`AggregationService.AggregateAsync` reads the snapshot and the entries after it on its own transaction
and returns only the aggregate. The head it saw is known locally (the last unfiltered entry, or the
snapshot's version when nothing follows) and thrown away. `IAggregate` carries no version.

Same-scope callers of `AggregateAsync` that matter: `SnapshotService.CreateSnapshot` calls it with
`toVersion` inside `SaveChangesAsync`, after the commit; `ChangeSetHandler` calls it twice, first bounded
to the command's `SourceVersion`, then unbounded. `ResiliencePipelineBehavior` retries an
`IResilientRequest` in the same scope, so per-scope state has to be cleared on a failed save as well.

Evidence: the implementation at `main` 398d20d; NextPA finding F-021 (two streams, 2026-10-09, facts at
v12/v13 after the removal at v11, and a soft deletion at v10 after v9).

## Goals / Non-Goals

**Goals:**
- A handler can state the version it decided on, and a save that would record after a later fact fails as
  a conflict.
- A host can give every read-then-write handler that protection without touching the handlers.
- No extra round trip on the save, and none on the append for the implicit form.

**Non-Goals:**
- Turning the option on by default. A handler that reads and then appends today succeeds against a
  concurrent writer; with the condition it gets a conflict, which on the `IMediator` path reaches the
  caller. That is a behaviour change for every consumer and belongs in a major version, if at all.
- Exposing the version on the aggregate or on `AggregateAsync`'s result. The implicit form needs neither,
  and the explicit form has `GetCurrentVersionAsync` for a caller who has no version of its own.
- Naming the colliding stream correctly in a multi-stream batch. `ConcurrencyException` names the first
  staged entry today; a conditional append does not make that better or worse.
- Wiring `IUpdateCommand.SourceVersion` into a condition. The three-way merge exists precisely to accept
  concurrent writes to other fields.

## Decisions

**New method names, not an overload.** `AppendAsync<T>(Guid, object, long, CancellationToken)` beside
`AppendAsync<T>(Guid, object, CancellationToken)` makes every existing call that passes `default` as the
third argument ambiguous (CS0121 — `default` converts to both `long` and `CancellationToken`), which is a
source break in a patch. `AppendAtVersionAsync<T>(streamId, expectedVersion, event, ct)` and
`AppendRangeAtVersionAsync<T>(streamId, expectedVersion, events, ct)` cannot collide, and putting the
version before the event reads as what it is: "at version N, append this".
- *Alternative:* a `StreamVersion` struct parameter. Rejected: `default` is ambiguous between two structs
  just as well.

**The unique index decides, not a read at save time.** A conditional append seeds `_streamVersions` with
the expected version instead of the head. If another writer has moved the stream, its entry occupies
`expected + 1`, and the existing index refuses the save; the existing catch turns that into
`ConcurrencyException`, clears the batch and counts the conflict. Nothing new on the save path, nothing
provider-specific, and both shipped providers already map the collision (spec *Two writers race on a
provider other than PostgreSQL*).
- *Alternative:* re-read the head inside the save's transaction and compare. Rejected: one more query per
  stream per save, and under read-committed it still races the other writer's commit — the index is the
  only check that cannot.

**An expected version above the head is refused at the append.** The index cannot see it (nothing
collides), and staging would leave a gap the spec forbids (*Versions are consecutive*). The explicit form
therefore keeps the one head read the first append makes today and compares: below or equal is staged,
above throws `ArgumentOutOfRangeException` naming stream, expected and head. A second conditional append
to a stream already staged in the batch must name the staged head, or it throws
`InvalidOperationException` — two different expectations for one stream in one batch are a caller's bug.

**The implicit form reads nothing.** A new internal scoped `AggregatedStreamVersions`
(`Dictionary<Guid, long>`) is injected into both services. `AggregationService` records the head of every
read with `toVersion is null`: the last entry's version from the unfiltered list (before
`AggregateEventSelector` drops events without an `Apply`, which is why the aggregate's last applied event
is not the head), the snapshot's version when no entry follows it, and 0 when the stream does not exist.
The last unbounded read wins, which is right for `ChangeSetHandler`'s bounded-then-current pair.
`EventSource.AppendRangeCoreAsync`, when the option is on and the stream is not yet staged, seeds
`_streamVersions` from the record instead of querying — one round trip fewer than today. A recorded 0
means the first append collides with whoever created the stream meanwhile, at version 1.
`ClearBatchState` clears the record, so a save — successful or failed — ends the expectation, and a
same-scope retry re-reads.
- *Alternative:* key the expectation on the aggregate instance. Rejected: aggregates carry no version and
  no identity beyond `Id`, and a handler may append to a stream it rebuilt as another type.

**The option lives in `EventSourcingOptions`, bound by `AddEventSourcing()`.** The section `EventSourcing`
is already reserved for write-side settings (`AddWriteStore(IConfiguration)` binds it). `AddEventSourcing`
takes no `IConfiguration`, so it uses `AddOptions<EventSourcingOptions>().BindConfiguration(...)`, the
pattern `AddProjectionReplayState()` uses. `EventSource` takes `IOptions<EventSourcingOptions>?` and the
record as optional constructor parameters, so the one hand-built instance in
`EventSourceTests.cs:51` keeps compiling.

**Default interface implementations throw `NotSupportedException`.** `IEventSource` is a published
abstraction that test doubles implement. A default keeps them compiling; throwing — rather than silently
appending unconditionally — keeps a double from pretending to give a guarantee it does not.

## Risks / Trade-offs

- [A host turns the option on and handlers that read a stream they never meant to guard start to
  conflict] → The conflict is the one the bus already redelivers under `MaxConflictRequeues`; the guide
  says which handlers notice (those appending to a stream another writer touches concurrently) and that
  the `IMediator` path surfaces it to the caller.
- [A handler reads stream A, appends to it, and the save fails because of an unrelated hot stream B in the
  same batch] → Unchanged from today: a batch is one unit, and a conflict on any stream discards it.
- [The record is filled by a read made for another purpose — e.g. a read model check through the
  aggregation service — and then guards an append the handler did not mean to guard] → That append was
  decided on that read; guarding it is the point. Documented.
- [`ConcurrencyException` crosses a silo without its stream id] → Unchanged (`FrameworkExceptionSerialization`
  carries the message); the message already names the stream.

## Migration Plan

Additive; a consumer adopts it by a version bump and, for the implicit form, by setting
`EventSourcing:AppendAgainstAggregatedVersion` to `true`. Rolling upgrade is safe: hosts on the old
version append unconditionally, as before. Rollback is turning the option off.
