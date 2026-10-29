---
title: "Operate the Orleans Execution Model"
description: "What an operator of the Orleans execution model has to know: a silo that dies hard, the grain directory, kept commands, stalled partitions, the reset, and the reminder profile."
---

# Operate the Orleans Execution Model

> **Derived page.** The behaviour described here is specified by the `orleans-execution` capability
> under `openspec/specs/`. That specification is the source; this page explains and illustrates it.
> Where the two disagree, the specification is right and this page is a bug.

[Migrate to the Orleans Execution Model](migrate-to-the-orleans-execution-model.md) says what a host
registers. This page says what an operator has to know once it runs.

## A silo that dies hard, and a replacement on another address

Orleans keeps a membership table of silos. A silo that is killed — no graceful stop — leaves its row
marked active. A new silo that starts on a **different** network address must reach every active silo
in the table before it may join. It cannot reach the dead one, retries for `MaxJoinAttemptTime` (five
minutes by default), and then fails with `OrleansClusterConnectivityCheckFailedException`. Shortening
the `IAmAlive` settings does not change this: they decide when a stale entry is logged, not whether a
joiner waits for it.

This is the shape an orchestrator produces when it replaces a crashed pod that was the cluster's **only**
silo with a new pod on a new address. There are three ways out:

1. **Run two silos.** A surviving active silo votes the dead one out after its missed probes, and the
   replacement joins as soon as that is done. This is the answer Orleans is built around.
2. **Restart on the same address.** A silo that comes back on the address it had declares its older
   self dead on start and is ready at once, and a timer that came due while it was down fires. An
   orchestrator with stable network identities gives this for free.
3. **Clean the membership table.** Delete the dead silo's row from the membership table before the
   replacement starts, or run the [reset](#reset-what-the-model-keeps) while no silo of the cluster runs.

A silo that is stopped rather than killed leaves nothing behind.

## The grain directory

The grains whose single activation must survive an unstable cluster — the store readers of projections
and sagas, singleton work, timer owners, stateful processes and the heavy-work permits — are placed in
the storage-backed directory the host registers with `AddStrataraOrleans`. Aggregate grains and command
runners use the runtime's built-in directory: a second activation of an aggregate ends in a concurrency
conflict at the store, which is the guarantee the bus workers have always relied on.

With Redis:

```csharp
var redis = StackExchange.Redis.ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("redis")!);

builder.UseOrleans(silo => silo
    .AddStrataraOrleans((s, name) => s.AddRedisGrainDirectory(name, options => options.ConfigurationOptions = redis)));
```

A silo without a directory under that name fails at start and names the call.

`AddStrataraOrleans` does a second thing: it publishes, in the silo's metadata, the roles and the singleton work
the silo registers, which is what [placement by role](#placement-by-role) reads. A silo that registers the
directory itself — `silo.AddRedisGrainDirectory(...)` under the model's name — and registers a role or a singleton
work fails at start with `117_120`, naming the roles and works it found and `AddStrataraOrleans`; otherwise the
other silos would place every role's grains on it. A silo that hosts nothing placed by role, such as an API host
that joins as a silo with the command dispatcher only, starts either way.

## Placement by role

A silo publishes the roles its composition registered — commands with `AddStrataraAggregateGrains`,
projections with `AddStrataraProjectionGrains`, sagas with `AddStrataraSagaGrains`, timers with
`AddStrataraDurableTimers` and an `ITimerOwners` — and the grains of a role are placed only on silos that
publish it: aggregates, command runners and the heavy-work pools on command silos, projection grains on
projection silos, saga and process grains on saga silos, timer owners on silos that have the timer ports.
Roles may therefore be split across silos, and a silo that registers a role registers every handler,
projection, process or timer port of that role. A call for a role no silo of the cluster registered fails
naming the role and the registration that adopts it, instead of activating where the role is missing.
Singleton work is placed the same way, on the silos that registered it.

## The response timeout

The runtime's response timeout (thirty seconds by default) bounds a caller's wait for one
grain call. A silo reads it from `SiloMessagingOptions.ResponseTimeout` and a host that calls into the cluster
from outside from `ClientMessagingOptions.ResponseTimeout`; configuring the shared `MessagingOptions` base
changes neither. On this model that is the wait for **one forwarded command**: a command a handler sends for
another aggregate, or a command dispatched through the mediator that names an aggregate. A handler that
cannot finish inside it is heavy work — mark the command `IHeavyCommand` — or the host sizes the timeout.
The aggregate's own order is not bounded by it: the commands accepted into one aggregate's order run to the
end however long the order takes, because the grain runs its order through a one-way call to itself.
The hand-over of a recorded command is not bounded by it either: whatever the command — one that names an
aggregate, a heavy command, or one that names no aggregate — the hand-over is answered once a silo has accepted
the command and its lease has started, and the command then runs on the silo however long it takes. A long heavy
unit therefore leaves no timeout in the log of the host that dispatched it or of the silo that runs it. A hand-over
that does time out means the silo did not accept the command in time; it is logged as a failed hand-over, and the
drain hands the command over again after the grace.

**A timeout does not mean the command did not run.** When a forwarded command's handler outlasts the
timeout, the caller gets a `TimeoutException` while the handler goes on in the aggregate's activation, runs to
its end and commits. A caller that retries on the timeout runs the command a second time — on an aggregate
that already appended, a concurrency conflict at best. Do not retry on a timeout; choose one of these instead:

- **Mark the command `IHeavyCommand`**, so it runs in the heavy pool and the caller does not wait for it.
- **Size `SiloMessagingOptions.ResponseTimeout`** — `ClientMessagingOptions.ResponseTimeout` on a host that
  calls in from outside the cluster — above the longest handler of a forwarded command.
- **Dispatch through `ICommandOutboxDispatcher`** where the caller need not wait for the result: the command is
  recorded before the call returns, and the call returns without waiting for the handler.

## Singleton work under a suspected death

Singleton work runs in one place while the cluster agrees on its membership. When a silo stops answering, the
work moves in steps, each bounded by a setting:

1. **Suspicion.** A silo that misses `ClusterMembershipOptions.NumMissedProbesLimit` probes (3) of
   `ProbeTimeout` (5 s) is suspected by the silo probing it.
2. **Declaration.** Once `NumVotesForDeathDeclaration` silos (2) have voted, it is declared dead in the
   membership table — with the defaults in the order of fifteen to thirty seconds after it stopped answering.
   A cluster too small for that many voters needs fewer: the runtime requires the lesser of the setting and
   half the active silos rounded up, so in a two-silo cluster the survivor's own vote declares the death.
3. **Takeover.** The work's keep-alive reminder now belongs to another silo and brings the work's grain up
   there on its next tick, within the work's `SingletonWorkOptions.KeepAlivePeriod` (one minute).
4. **The declared silo stops.** A silo that is still running learns of its own declaration at the latest at
   its next read of the membership table, every `TableRefreshTimeout` (one minute), usually sooner through
   gossip, and stops itself.

With the defaults a failover therefore completes within about a minute and a half. Between steps 3 and 4 the
work **may run on two silos at once** — at most one table refresh period plus one run, and only as long as the
declared silo can still read the membership table. A silo partitioned from it may never learn of its
declaration — gossip reaches it only from silos it can still talk to — and runs the work until something stops
it, so the table refresh bounds the overlap only where the table is reachable. Write a work so that an
overlapping run does no harm: claim what it processes with a compare-and-set in its own store, as the
framework's outbox drain claims recorded commands, or make each run idempotent. The integration suite measures
the takeover under the test profile (a five-second keep-alive) and allows it three minutes.

### Settings per work

A work's settings are the host's settings for every singleton work with the work's own registration applied on
top. `services.Configure<SingletonWorkOptions>(...)` sets the default for every work; the callback given to
`AddStrataraSingletonWork<TWork>(configure)` or `AddStrataraSingletonWork<TWork>(name, configure)` sets that work
alone and never reaches another:

```csharp
builder.Services
    .Configure<SingletonWorkOptions>(options => options.KeepAlivePeriod = TimeSpan.FromMinutes(2))
    .AddStrataraSingletonWork<OutboxDrainWork>(OutboxDrainWork.WorkName)
    .AddStrataraSingletonWork<NightlyCleanupWork>(options => options.KeepAlivePeriod = TimeSpan.FromMinutes(5));
```

The drain is kept alive every two minutes, the cleanup every five. A work registered twice runs once, with the
settings of the later registration — a later call without a callback leaves the work on the host's default. Each
work's settings are validated when the host starts, and an invalid one fails the start naming the setting and the
work.

## Sends between aggregates

A handler running for one aggregate may send a command for another; the command runs in the other
aggregate's activation, after the command running there, and the sender waits for it. The sends must form
a directed acyclic graph: a send that names an aggregate whose turn is waiting on the sending chain —
A sends to B, and B's handler sends back to A — is refused at once with a message naming both aggregates,
B's command fails with the refusal and A's command with B's failure. Nothing waits for the response timeout.
Where a handler has to reach back, dispatch through `ICommandOutboxDispatcher` instead: the command is
recorded and handed over without waiting, in its own turn.

## Cancelled calls report what the callee did

Orleans completes a grain call whose token is cancelled with a cancellation the moment the token fires,
whatever the grain it called does. A saga step that committed as the timer tick or saga reader calling it
stopped would then look cancelled, and would run again. The execution model's registrations
(`AddStrataraOrleans`, `AddStrataraOrleansCommandDispatcher`, `AddStrataraAggregateGrains`) set
`MessagingOptions.WaitForCancellationAcknowledgement` for the silo and the client, so a cancelled call waits
for the callee's answer: the cancellation still reaches the callee, a callee that stops before it commits
answers with it, one that has committed answers with its outcome, and one that does not answer ends the
call with a `TimeoutException` at the response timeout — or with a `SiloUnavailableException` once its silo
is declared dead, whichever comes first — rather than at once. The setting is the host's for every grain call that carries a token, the host's own included — a
call made with a web request's abort token, for instance, now waits for its grain's answer; calls without
a token are unaffected. A host that sets it back after these registrations gives that guarantee up.

## Failures that cross silos

A command forwarded to an aggregate on another silo can fail there. Orleans carries an exception from
one silo to another with its type only when it is told to, and a chain with one exception it refuses —
a database or a broker exception inside the framework's own — does not cross at all. The execution
model's registrations therefore let every exception type cross, so the framework's failures arrive as
they were thrown, their inner exceptions included. A bus worker that forwarded a command still sees a
`ConcurrencyException` as a conflict and a `CommittedEventsNotPublishedException` as a save not to run
again. What crosses is the type, the message, the stack trace and the inner exceptions; the properties
of the framework's exceptions read empty on the far side, and their messages carry the same facts. A
`StrataraValidationException` is the exception: its `Failures` cross with it — field, message and code,
not the attempted value — so the problem-details handler still names the fields to correct, and its
message stays generic, because a failure's message may quote the input and exception messages are
logged. During a rolling upgrade from an earlier version, silos of the two versions cannot read each
other's validation failures: such a call fails on the caller's side with a serialization failure until
every silo runs the new version. The
setting only decides what a silo sends; what a silo accepts is Orleans' own type filter, which stays as
it is. A host that sets its own `SupportedExceptionTypeFilter` on
`Orleans.Serialization.ExceptionSerializationOptions` after these registrations replaces the
framework's, and must keep letting exception chains through, or a framework failure from another silo
arrives as a serialization failure again.

## Kept commands

A recorded command is bounded as a bus message is, by the two bounds of `MessageRetryOptions`:

- **A handler that keeps failing** runs `MaxDeliveryAttempts` times in all and is then kept. The record counts the
  hand-over of the dispatch as the first attempt when it is written, and each resumption adds one, so a command kept
  under the default bound of 3 carries `attempt_count = 3`. A host that dies after the record and before that
  hand-over has used the attempt all the same, as a bus message delivered to a consumer that crashes has used its
  delivery: such a command runs one time fewer, and under a bound of 1 it is kept for an operator without running —
  kept, never lost.
- **A handler that fails on a concurrency conflict** — the event store's `ConcurrencyException`, after the
  in-process conflict pipeline has retried, which is what the bus transports count as a conflict — counts the
  conflict in `conflict_count` and gives its attempt back, so conflicts never use up the delivery bound. A command
  that has been resumed after a conflict `MaxConflictRequeues` times (100 by default) and conflicts again is kept,
  which is when the bus moves such a message to its dead-letter destination. Any other exception, a provider's own
  conflict exception included, is a failure, as on the bus.
- **A handler stopped with its silo**, and a command still queued behind it that the stop gives up, gives its attempt
  back too: a stop never uses up the delivery bound.

A kept command stays in the outbox table with both counts and its last failure, and the commands after it are
still resumed. Find the kept commands with:

```sql
SELECT id, aggregate_id, attempt_count, conflict_count, last_failure
FROM outbox_entry
WHERE kept_at IS NOT NULL;
```

Once the cause is fixed, return a kept command; it is resumed with its counts starting over and gets the whole
delivery bound again:

```sql
UPDATE outbox_entry
SET kept_at = NULL, attempt_count = 0, conflict_count = 0, last_failure = NULL
WHERE id = @id AND kept_at IS NOT NULL;
```

A failing handler is seen before the command is kept: every attempt that fails is logged as `117_112`
with the command's identity, its type and the aggregate it names, and a hand-over that fails as `117_113`;
the resumption that follows logs `117_004` with the attempt number, and the keep logs `117_104` with both counts.

A resumed command is run by one runner. Two resumptions can claim the same command — two drains while the singleton
fails over, or the drain beside a bus outbox worker during adoption — and hand it over twice; so can a hand-over
that arrives after the command completed. The hand-over carries the stamp of the claim that issued it, and the
receiver takes the command over only if the record still carries that stamp: the first receiver moves it, and a
hand-over that finds it moved, or finds the record gone, is dropped without running the handler and logged as
`117_124` (Debug). The dispatch's own hand-over carries no stamp; when it arrives late — past a sixth of the grace
after the dispatch — its first renewal also checks that the command is still recorded and not kept, and drops it
otherwise, so a hand-over delayed past a resumption that already completed the command does not run it again. A
hand-over from a silo still on 4.1 carries no stamp and is only checked that way, so the fence holds once every silo
runs 4.2. A store that fails the fencing renewal fails the hand-over rather than dropping it, and
the drain hands the command over again after the grace.

Commands that one scope dispatches to one aggregate are resumed in the order they were dispatched, however long
each took to be recorded: the dispatcher takes the record's time from the registered `TimeProvider` when the
dispatch starts, at least a millisecond after the previous one of the scope for that aggregate, and the drain
resumes due commands by that time. Whether a command is due is judged on the same `TimeProvider`.

Under `BusEnvelopeIntegrityMode.Strict` a command is also kept, at once and without an attempt, when its record
carries no signature (`117_117`) or a signature that does not verify (`117_118`); `last_failure` says which. Returning
such a command verifies its record again, so return it only once the record is correct — a record altered in storage
stays kept. See [bus envelope integrity](hmac-bus-envelope.md).

A backlog is resumed as fast as the handlers take it: while a pass of the drain finds `OutboxDrainOptions.BatchSize`
commands due, the next pass follows at once, for at most `OutboxDrainOptions.PollingInterval`, and the next run
continues. One pass claims its batch in two statements.

## A partition that stops advancing

An entry a projection or saga cannot apply stops its partition. The checkpoint stays before the entry,
the failure is logged with the entry's identity (`117_101`, every attempt `117_102`) and counted in
`orleans.reader.stalled`, and the entry is tried again on every wake-up and poll — nothing after it in
that partition advances until it passes. A read that fails before any entry applied — the store
unreachable, a checkpoint the reader refuses — counts as a stall of the same partition and is logged as
`117_103`, whichever wake-up or poll started it; the next one reads again. The event identifiers are
listed in the [log events schema](../reference/log-events-schema.md). A missing prerequisite from another
partition is retried under the preceding-fact policy in the same way.

Every store-reading saga reads with a checkpoint of its own, under the consumer `sagas:<SagaName>` — the saga's
type name, as the saga's logs name it — so a saga that throws on an entry stops **only its own** reading of the
partition. Every other saga applies the entry once and goes on past it, and is not run again because of that
failure; the stall is logged and counted under the failing saga's consumer, and that saga is retried, without a
bound, until it passes. A stateful process reads the same way, under its own consumer, and hands its facts to the
process's grains. A saga has no retry bound on the execution model and no dead-letter queue: fix the saga, or the
cause of its failure, and it continues from the entry it stopped at.

A reader per saga costs what a reader per projection does. With S sagas and P partitions a saga silo runs S×P
reader activations and S×P keep-alive reminders where it ran P, a silo's start ensures S×P readers, and every wake-up
costs a read of the store per saga it concerns ([wake-ups after a commit](#wake-ups-after-a-commit)). A reader's first catch-up after it is activated asks the checkpoint store once
per saga of the host whether that saga has a checkpoint in the partition, so a cold start of the cluster costs S×S×P
such queries. Size the read and write stores' connection pools for that many readers catching up at once, as for
projections. A silo's command handlers, readers, drain and hand-over renewals all draw from the pools of the
connection strings they use, and a connection string that names no `Maximum Pool Size` gets 15
([connection pools](../getting-started/di-composition.md)): name a size in the silo's connection strings that
covers the roles it runs. A stateless saga's reader passes over an entry of a type the saga does not handle without
deserialising it.

A saga's checkpoint is keyed by its name. A saga that has no checkpoint in a partition — one registered after the
deployment's sagas have read, or every saga on the first start after an upgrade from 4.1.x — starts where the
host's sagas read there: at the furthest checkpoint a saga of the host holds, or the checkpoint the sagas shared
before 4.2.0, and at the beginning of the store only where no saga has read. A saga added to a running deployment
therefore never runs its side effects for the store's history. Renaming a saga class makes it a new consumer,
which starts at the same point — not at its old checkpoint. The old name's reader is still brought back by its
keep-alive; on a silo that registers no saga of that name it unregisters its keep-alive, logs `117_126`
(Information) naming the saga and the partition, reads nothing and deactivates, so a silo that still registers the
saga — a rolling deployment — can host it. The old name's checkpoints stay in the read store until the
[reset](#reset-what-the-model-keeps) of a host that registers the name, or a delete the host owns. Two saga
classes of the same type name in different namespaces would share one consumer, so a host that registers them does
not start, naming both; give each saga a type name of its own.

The saga readers need the checkpoint store to tell a missing checkpoint from one at the beginning and to write a
saga's first checkpoint without replacing one: `IProjectionCheckpointStore.FindAsync` and `CreateAsync`. The
framework's store (`AddStrataraProjectionCheckpoints`) implements both. A store of the host's own that does not
fails every saga reader's first catch-up with a message naming the two members, logged as `117_103`; projections do
not use them.

Under the portable reader a partition also stops at an entry that has **no partition position**: a process
appended it without `PartitionCounterInterceptor`. The logged failure names the entry, the interceptor and
`PartitionCounterBackfill`. Add the interceptor to the write context of that process, then run
`PartitionCounterBackfill.RunAsync` once; the partition continues from where it stopped. The backfill gives the
entry a position after everything the partition had positioned, so a stream whose later versions were appended
with the counter meanwhile is read with the late entry after them; a read model that then stops on a missing
preceding fact is rebuilt. The backfill works in batches, each under the partition's counter for its own
transaction, so it does not hold the counter — or the appends behind it — for a long history; an append that
lands between two batches is positioned before the entries the next batch positions, which is the same
inversion, on a stream that is being repaired while it is written to. A host that refuses
to start naming a partition counter beyond its partition count was configured with a lower count than the
store was counted with; restore the count.

A checkpoint is advanced only from the position its reader last saw and never under another reader's name. An
activation that outlived its successor — a silo suspected dead that is still writing — is refused with a message
naming both positions, logged as `117_103`, and reads the checkpoint again instead of rewinding it; a write under
another reader's name is refused naming both readers. A host that switches readers resets its checkpoints first.
For **projections** that can be done inside the running cluster, because returning a checkpoint to the beginning
is the one write that is not held to the reader's name: rebuild the read model with
`IProjectionRebuilder.RebuildAsync`, or run a full replay, and the checkpoints are taken over by the host's own
reader. The **sagas'** checkpoints are not touched by either verb — a saga is not rebuilt, its effects having left
the deployment — so a host that switches readers or changes its partition count while it registers sagas needs
the [reset](#reset-what-the-model-keeps) with the deployment stopped.

Under the native reader, a host that lowers its partition count and resets its checkpoints as documented may
still have keep-alive reminders of readers beyond the new count, if the reminders were not reset. Such a reader,
when a reminder brings it back, retires: it unregisters its keep-alive, logs `117_005` naming the consumer, the
partition and the count, reads nothing and counts no stall. The event appears once per retired reader.

The saga reader a 4.1.x deployment shared between all its sagas — keyed `sagas/<partition>` — retires the same way
on a 4.2.0 silo: brought back by the keep-alive reminder the old deployment registered, or by a call of a silo that
still runs 4.1.x, it unregisters its keep-alive, logs `117_125` (Information) naming the partition, and reads
nothing. The checkpoint it left under `sagas` stays where it was — it is where the sagas start — until the
[reset](#reset-what-the-model-keeps) removes it.

A projection removed from the host, or renamed, leaves the keep-alive reminders of its readers behind. Such a reader,
when a reminder brings it back on a silo that registers no projection of its name, retires: it unregisters its
keep-alive, logs `117_136` (Information) naming the projection and the partition, reads nothing, counts no stall and
deactivates, so a silo that still registers the projection — a rolling deployment — can host it. The event appears
once each time such a reader is brought back, as `117_126` does for a saga's reader: once per reader after a
deployment that removed the projection everywhere, and again for each wake-up that a silo still registering it sends
during a rolling deployment. A retired reader leaves its checkpoints where they were, and
a host that registers the projection again resumes it from them rather than from the beginning of the store. The
[reset](#reset-what-the-model-keeps) does not remove them, because it touches only the consumers the host registers.
Once no silo registers the consumer any more, delete its rows from the checkpoint table yourself —
`DELETE FROM projection_checkpoint WHERE projection = 'OrderSummaryProjection'`, with the projection's name, or
`sagas:<SagaName>` for a saga. The column is `projection` under the snake-case names the PostgreSQL registrations
apply; a read store mapped without them names it `Projection`.

A rolling deployment that adds a projection or a saga can place one of its readers on a silo of the earlier version,
which does not register it: the reader retires there and unregisters the keep-alive the new silo registered. It still
reads on every commit's wake-up, so no fact is missed, but it has no keep-alive to bring it back after a silo is lost
until a silo that registers the consumer starts again. Where that matters, restart one silo once every silo runs the
new version. A rebuild or a full replay that meets such a reader fails naming it and releases the readers it paused,
because the reader cannot be held paused on a silo that does not run it; run it again once every silo runs the new
version. A wait for the projection counts such a partition as behind, not as a mark that does not apply.

## A rebuild or a replay that does not finish

A projection's rebuild and a full replay pause the projection's store readers while they return the checkpoints
to the beginning and empty the read model. The pause belongs to the process that took it and lasts only while
that process renews it — every twenty seconds, for a lease of sixty. A process that dies part-way through, or
whose silo is lost, leaves the readers paused for no longer than the lease and one poll interval: each reader then
resumes by itself, logs `117_123` (Warning) naming the consumer and the partition, and reads from whatever its
checkpoint says. Nothing needs a silo restart. A rebuild that was cut short is still not finished: run it again.
A resume that is repeated — a retry whose first answer was lost — releases only its own pause, so it never lets a
second rebuild's readers go early.

A reader can still resume before its rebuild ends: its pause lapsed because the rebuilding process could not
renew it in time — a long garbage-collection pause, a saturated silo — or its activation moved to another silo and
forgot the pause. What such a reader applied before the read model was emptied would be lost with the truncation,
so a rebuild and a replay wait for any batch such a reader still has in flight and then return the checkpoints to
the beginning a second time, after the truncation, and the reader reads those facts again. A fact it applied between the truncation and that second reset is applied
**twice**; a rebuildable projection must therefore apply idempotently — the property a full replay already
requires of every projection that reads the store. A `117_123` logged during a rebuild is the sign that it
happened. This protection rests on the checkpoint store refusing an advance from a position it no longer holds:
the framework's store does, and a checkpoint store of your own must override
`IProjectionCheckpointStore.AdvanceAsync` with the same guard — its default replaces the position unchecked.

A host that registers `AddReadModelRestore<TReadContext>()` copies the read context's tables before a full replay
empties anything — the checkpoints of the store readers among them, in the same snapshot as the read models — and
writes them back when the replay fails or its host stopped (see
[Keep the read models a failed replay would leave behind](write-a-projection.md#keep-the-read-models-a-failed-replay-would-leave-behind)).
The readers then resume from the restored positions over the restored read models, so nothing is applied twice and
nothing is skipped. A rebuild of one projection is not covered: it empties only that projection's read model, and a
rebuild that fails part-way resumes from the beginning.

During a rolling upgrade from 4.1.x, a rebuild or replay started from a silo still on 4.1.x pauses the readers of
an upgraded silo for at most ten minutes — it cannot renew — and returns the checkpoints to the beginning only
once, and one started from an upgraded silo cannot pause a reader still hosted on 4.1.x and fails naming it. Rebuild
once every silo runs the new version.

## Waiting for a projection

`IProjectionProgress` lets a host wait until a projection has applied what it committed (see
[Read your own write](write-a-projection.md#read-your-own-write)). Three things decide how it behaves in operation.

**A mark costs one query per partition it constrains.** `MarkAsync()` asks the commit-order reader for the highest
committed position of every partition, one query each, in sequence — sixteen at the default partition count;
`MarkAsync(streamIds)` asks only for the partitions those streams fall into, and a wait for it is not held up by a
partition that lags or is stopped elsewhere. On the native
PostgreSQL reader each walks the transaction-id index from the top until it meets an entry of its partition: cheap
for a partition with recent entries, a walk of the whole index for one that has none. A host that takes a mark on
every request pays that on every request; take one only where a read follows a write. A reader of your own answers
the mark through `ICommittedPositionReader.CommittedHeadAsync`, whose default is `HeadAsync`; a reader that holds its
head back below what is committed, as the native PostgreSQL reader does, must override it, and a decorator around a
reader must forward it, or a wait may answer before the projection has applied the write.

**A wait asks each partition's reader and is answered when it gets there.** Each call waits at most two seconds
on the grain — a waiting call holds that grain's deactivation for as long — and a longer wait asks again. It answers
`Reached` only when every partition says so in the same round, so a rebuild that began during the wait is never
missed. Nothing polls the read store, and a wait for a projection that is already through answers without waiting.

**What holds a wait back.** On the native PostgreSQL reader nothing is read past a transaction that is still open,
so a long-running transaction on the write store — one that appends nothing included — holds every projection's
readers back, and a wait for a fact committed after it times out although the fact is long visible. `TimedOut`
with `orleans.reader.lag` climbing on every partition is that sign; find the transaction in `pg_stat_activity`.
`Stalled` is a partition stopped at an entry; `Suspended` is a rebuild or a replay in progress, and the
[section above](#a-rebuild-or-a-replay-that-does-not-finish) says what to do when one does not finish.

The time a host waits is its own latency budget: a request path usually waits a few seconds and answers "try
again" on anything but `Reached`; a background job may wait longer.

During a rolling upgrade from 4.4.5 or earlier, a silo of the older version does not answer the wait, and a wait
whose partition lives there fails with the grain call's error. Adopt the wait once every silo runs 4.4.6.

## Wake-ups after a commit

A commit wakes, in the partitions it touched, the readers whose projection or saga applies one of the committed
event types. On a silo with a store-reading role the role's own registration does it. On any other host that
commits — an Orleans client that runs handlers in process, or a command silo without a reader —
`AddStrataraStoreWakeUps()` does it through the cluster: one call per role and key of the commit, to a grain on a
silo of the role, which wakes the readers on that silo that the commit concerns. The event types travel beside the
call, so the call itself is the one earlier versions make: a wake-up from a host of 4.6.0 or earlier wakes every
reader of the partition, and so does a silo of 4.6.0 or earlier that a newer host wakes. Nothing waits for a wake-up.

Three kinds of reader are woken by every commit in their partitions: a stateful saga process, which decides from
the event itself whether it handles it; every reader, when the commit holds an event type the silo cannot resolve
or one an upcaster is registered for, since the type it upcasts to is known only from the payload; and every
reader of a host whose upcaster pipeline is its own rather than the framework's. A reader woken in vain reads and
applies nothing.

### What a reader writes, and what a poll reads

A projection's reader that reads entries and applies nothing for them keeps the position past them and answers a
wait for its projection from it, but writes it as its checkpoint only with the next batch that applies something,
once `ProjectionGrainOptions.IdleCheckpointInterval` (30 seconds by default) has passed, or when it deactivates.
A reader whose silo dies before that reads those entries again after it is brought back and applies nothing for
them. `TimeSpan.Zero` writes the checkpoint after every batch, as releases before 4.6.1 did. A rebuild or a replay
pauses the readers and discards a position they hold back, so a checkpoint returned to the beginning is not moved
past what the rebuild must read again. A saga's reader always writes its checkpoint once it has read a batch: a saga
added to a running deployment starts at the furthest checkpoint the host's sagas hold, and must not react to
anything before the position they reached.

A reader's poll reads the store only when its partition holds something past the reader's position. Each silo
asks the store for a partition's head at most once per half of the poll interval, whichever of its readers asks,
so an idle silo costs the store a few queries per partition and poll interval rather than one read per reader. A
fact whose wake-up was lost is therefore applied within about one and a half poll intervals. A committed position
reader of your own that does not override `ICommittedPositionReader.HeadAsync`, whose default walks the partition,
is not asked: its readers poll as before. While a partition moves with facts a reader does not apply, that reader
still reads once per poll.

A role no silo of the cluster hosts is set aside for `StoreWakeUpOptions.AbsentRoleRetry` (ten seconds by default)
instead of being called on every commit, and the host logs `117_131` once. A placement can also fail for a moment while
a silo of the role joins, so the period is short. No more than 256 wake-ups of one role await their answer at once;
a host whose wake-ups go to a silo that hangs loses the ones beyond that, at debug, rather than piling up calls. Register
`AddStrataraStoreWakeUps()` after the composite that registers the bus dispatcher (`AddBackendServices` or
`AddCommandServices`), as with the store-reading roles; the composite also brings the replay state the wake-ups read,
and it opens no broker connection while nothing publishes. When a silo of the role joins, the first
commit after the period wakes it, and the host logs `117_132`. Until then, the new silo's readers find the commits
by their poll. A wake-up that cannot be sent for any other reason, such as a silo of an earlier release that has no
wake-up grain, is logged at debug as `117_121`.

## What to watch

Every instrument is published under the meter `Stratara` with the names in
`ApplicationDiagnostics.Metrics`, tagged with the projection and the partition where they apply:

| Instrument | What it says | Alert when |
|---|---|---|
| `orleans.reader.stalled` | Partitions currently stopped at an entry or a failed read | above zero for longer than a retry deserves |
| `orleans.reader.lag` | Age of the oldest entry a partition has not applied | above the bound the host promises its read models |
| `orleans.reader.applied` | Entries applied | flat while `orleans.reader.lag` grows |
| `orleans.intent.recorded`, `orleans.intent.resumed` | Commands recorded and resumed after a lost hand-over | resumptions climb while nothing was killed — hand-overs are being lost |
| `orleans.intent.kept` | Commands kept for an operator | every increment |
| `orleans.completion.flushed`, `orleans.completion.failed` | Completed commands removed, and removals that failed | failures above zero |
| `orleans.heavy.permits_in_use` | Heavy units running under a permit | at `ClusterWideLimit` for longer than the units take |

The log events to route to an alert: `117_101` and `117_103` (a partition stopped), `117_104` (a command
kept), `117_111` (recorded commands on a silo without an intent store), `117_112` and `117_113` (a failing
attempt or hand-over), `117_114` (a heavy unit running outside the cluster-wide bound), `117_123` (a store
reader's pause that lapsed — a rebuild or replay whose process died or stalled; run it again), `117_119` (a run of a
singleton work that threw — the work runs again at its next period, so a work that fails every run logs it every
period). `117_008` (a handler
stopped with its silo) explains a second run of a command or a timer after a deploy. The runtime's own
`Could not deliver reminder tick` for a store reader, a singleton work or a durable timer is worth a look outside a
deploy: they answer their tick at once while they read, run or handle it, so the line means the silo could not reach
the grain in time. While a silo stops or a grain moves, a grain being deactivated waits for its running catch-up, run
or handler, and a tick in that window can still time out. `117_137` (firing a durable timer failed — its handler, or the owner check or unregistration
around it; the timer fires again after its retry period, so a timer that fails every time logs it every retry period)
belongs with the alerts. `117_116` and `117_118` (a
recorded command whose signature does not verify) mean the record was altered or the key differs; alert on them. Worth routing to a
dashboard rather than an alert: `117_006` and `117_007`, logged once when a full replay starts holding recorded
commands back and once when it releases them — a command that waits for the length of a replay is waiting, not
lost; `104_015` and `104_016` from the replay state, logged once when the host can no longer refresh
the replay marking from Redis and answers from what it last knew, and once when it can again; and
`104_017` and `104_018`, logged once when the host cannot subscribe to the marking's announcements
and sees a change on the refresh period only, and once when it is subscribed again. The whole band is
listed in the [log events schema](../reference/log-events-schema.md).

## The replay marking costs no round trip

The model asks whether a full replay is active on every path that matters: once per dispatched command,
once per commit before the readers are nudged, at the start of every catch-up and before every batch a
store reader applies, and once per drain pass. None of those asks Redis. The answer comes from the
host's memory and is refreshed asynchronously — at once when another host announces a change over the
shared connection, and every `ProjectionReplay:RefreshSeconds` (default 5) regardless, which is also how
a marking that lapsed is noticed. A host can hold the model to that: a `RedisTimeoutException` on
`GET stratara:projection:replay:active` under load is a sign of a release before 4.4.2, where every
one of those reads was a blocking round trip and a two-core host could starve its thread pool on them.
What the model does with the answer is unchanged: a replay holds recorded commands back, skips the
wake-ups and suspends the readers, and the readers' pausers hold them regardless of the flag. A host
that cannot reach Redis keeps its last answer and logs `104_015` once; `104_016` says it reads the
marking again. A host that cannot subscribe to the announcements logs `104_017` once and sees a change
on the refresh period only until `104_018` says it is subscribed again.

## Reset what the model keeps

`IExecutionModelReset` clears everything the model keeps beside the event stream for the host's
deployment: the reminders of its service, and with them every durable timer; the membership rows of its
cluster; the checkpoints of the projections and sagas it registers, with the one its sagas shared before 4.2.0;
and the grain directory's entries,
through a callback the host supplies because the directory is its choice. The event stream is never
touched, and a host started afterwards rebuilds those checkpoints from it. The report counts what was
removed of each.

```csharp
var orleansDb = builder.Configuration.GetConnectionString("orleans")!;
builder.Services.AddStrataraExecutionModelReset<AppReadDbContext>(
    orleansDb,
    async (services, cancellationToken) =>
    {
        // With the Redis directory: remove the cluster's keys and report how many.
        var multiplexer = services.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();
        var keys = multiplexer.GetServer(multiplexer.GetEndPoints()[0]).Keys(pattern: "*my-cluster*").ToArray();
        return keys.Length == 0 ? 0 : await multiplexer.GetDatabase().KeyDeleteAsync(keys);
    },
    schema: "public");

await using var scope = app.Services.CreateAsyncScope();
var report = await scope.ServiceProvider.GetRequiredService<IExecutionModelReset>().ResetAsync();
```

Run it while no silo of the cluster runs: a running silo writes its membership and reminders back. The
reset is a scoped service, like the store readers whose names it reads; resolve it from a scope, as
above — a host built with scope validation on refuses to resolve it from the root. Name the schema the
runtime's scripts ran under where it is not the connection's default; a reminder or membership table
absent under that schema fails the reset naming the table, rather than reporting that nothing was
removed. The three runtime tables are cleared in one transaction; a failure after them — in the
checkpoint delete or the directory cleanup — leaves the checkpoints and the directory as they were, and
the reset can be run again.

Resolve it from the host's own composition — the one that calls `AddStrataraProjectionGrains` and
`AddStrataraSagaGrains`. The checkpoints it removes are those of the projections and sagas registered
there; a tool that registers none removes no checkpoint and reports zero. Another consumer's checkpoints
in the same read store stay, and so do those of a projection the host no longer registers: nothing reads
them, and removing them is a delete the host owns.

### Seeding instead of resetting

The reset brings a deployment back to "nothing remembered", and a host started afterwards reads the whole
store again. A host whose read models are current does the opposite before its first start: it seeds a
checkpoint at the head for every consumer it registers, with `IStoreReaderSeeding`, resolved from a scope
like the reset — see [Start on a populated store](migrate-to-the-orleans-execution-model.md#start-on-a-populated-store).
A checkpoint at the beginning counts as absent and is seeded, and the seeding takes no list of consumers, so
seeding after a reset puts every consumer back at the head and nothing is re-read: where a full re-read is
wanted, reset and start without seeding. Where only some read models are to be rebuilt, seed and rebuild
those afterwards with `IProjectionRebuilder`.

### Sharing a read store

A checkpoint belongs to a consumer and a partition, not to a deployment. Two deployments can keep their
checkpoints in one read store only when no projection name is registered by both. Each store-reading saga is a
consumer of its own, but the sagas of all deployments sharing a read store still count as one set: a saga without a
checkpoint starts from the checkpoints the other sagas hold there, so a second deployment's sagas would start from
the first's positions, and two deployments registering a saga of the same name would overwrite each other's. At
most one deployment sharing a read store runs `AddStrataraSagaGrains`.

## Reminder profile and clocks

A deployed silo runs Orleans' reminder defaults. Keep-alive periods and the timer retry period are kept
as reminders, so they must be at least the runtime's minimum reminder period; the host fails at start
otherwise. Lowering `ReminderOptions.MinimumReminderPeriod` is for tests that need to observe a reminder
within seconds.

A timer's due time is computed on the host that registered it and its tick runs on a silo with a clock of
its own. A tick earlier than the due time by less than `DurableTimerOptions.DueTolerance` fires instead of
waiting a whole retry period.

A store reader, a singleton work and a durable timer answer their reminder tick while they read, run or handle it. A
reader's catch-up, a work's run and a timer's handler may last longer than the response timeout, and the tick, like a
silo's start that makes sure each reader and work runs, does not wait for it. A catch-up of a large backlog, a long
singleton run or a long timer handler therefore leaves no reminder line in the silo's log.

A timer fires once however long its handler runs. A tick that arrives while the handler still runs does nothing, and
the timer is unregistered once the handler has completed — or stays registered, and fires again on another silo, when
the handler was stopped with its silo (`117_008`). A handler that fails — or an owner check or unregistration around it
— is logged as `117_137` (Error) with the owner, the purpose and the exception; the timer stays registered and fires
again after its retry period, so a handler should be safe to run again. Keep timer handlers short where the work can wait for a command: a heavy command spreads long
work across the cluster, a timer's handler holds its owner's grain. A handler may register its own owner and purpose
again, with the due time it fired for or a later one; the new timer is kept.

An owner id is at most 139 characters and a purpose at most 130: the reminder table holds 150 for each, with the
grain type's name or the due time taking the rest. A longer one is refused by `IDurableTimers` on every member,
with a message naming the limit.

An `ITimerHandler` runs in a fresh scope with **no session**, because no request started the work. A handler
that dispatches a command or appends an event sets one — and the shape for it is
`SessionContext.ForPlatform(tenantId)`, which names the platform as the actor, the tenant the work is for as the
data owner, and carries the causation id the store requires. Only the handler knows which tenant its owner id
belongs to, so the framework cannot set it for you. Strict tenant isolation lets that session through without
consulting the cross-tenant authorizer; see
[Enforce Tenant Isolation](enforce-tenant-isolation.md#work-the-platform-starts-is-not-cross-tenant).

## Stopping a silo

A silo stopped gracefully waits for the handlers running on its grain paths — a command in its aggregate's
activation, a command without an aggregate, heavy work, a timer — for the deactivation budget,
`GrainCollectionOptions.DeactivationTimeout` (30 s by default), a setting the host sizes. A handler that completes
within it is never interrupted. Past it, the `CancellationToken` each handler received is cancelled, and each
stopped handler is logged as `117_008` with what it was running:

| Path | What happens after the stop |
|---|---|
| Recorded command | Its hand-over lapses; another silo resumes it after `OrleansDispatchOptions.IntentGrace`, and the attempt its resumption counted is given back, so a stop never uses up the delivery bound |
| Forwarded command | The caller's dispatch fails with a message saying the silo stopped; dispatch it again |
| Heavy work | A unit still waiting for a worker or a permit stops waiting; a running one is resumed like a recorded command |
| Timer | The timer stays registered and fires on the next silo that serves its owner |

Pass the token to every await of a handler, and observe it before the handler commits rather than after: a
handler stopped after its commit runs again, which it tolerates as it tolerates any at-least-once delivery. A
handler that ignores the token runs to its end, and the silo waits for it as long as the runtime allows. A silo
that dies hard has none of this; its work is resumed as after any crash.

## Heavy commands and their aggregate

A heavy command runs in the bounded heavy-work pool, not in its aggregate's activation, so a long unit
does not hold back the aggregate's other commands. It therefore keeps no order with them: a command
dispatched after it for the same aggregate does not wait for it, and where both append, the store's
version check refuses the later one, which is resumed as a conflict, within `MessageRetryOptions.MaxConflictRequeues`. Mark a command heavy only where it rarely meets a stream of other commands on its
aggregate.

The pool is as many pools as `HeavyWorkOptions.ClusterWideLimit` needs, eight slots each, placed on silos
of the command role; the permits bound the cluster. A heavy hand-over is leased from the moment the pool
accepts it — while it waits for a slot, while it waits for a permit and while it runs — so a burst that
queues units for longer than `OrleansDispatchOptions.IntentGrace` hands none of them over twice. The
lease and the permit are renewed from timers of their own, off the activation's scheduler, so a handler
that computes without yielding is renewed all the same: however long a handler runs, and whether or not it
yields, it runs once.

### A burst of heavy work

Waiting costs a recorded command nothing. A burst of heavy commands many times the cluster-wide limit becomes a
backlog that is worked off, and only a handler that runs and fails counts against
`MessageRetry:MaxDeliveryAttempts`:

- **The pool holds a bounded number.** One pool holds at most `HeavyWorkOptions.QueuedLimitPerPool` commands
  (256 by default), running and waiting together; set it no lower than a pool runs at once. A hand-over beyond it is
  refused and logged at debug (`117_133`), and it costs no attempt. The command stays in the store and is handed over again after the grace, at most a
  drain batch at a time. The whole cluster holds `pools × QueuedLimitPerPool` heavy commands in memory, and the rest
  wait in the table, where they cost nothing.
- **Holding costs one statement per renewal period per thousand commands.** A silo renews every command it holds —
  in an aggregate's order, in a runner, in the heavy pool — in batches every third of the grace, through
  `ICommandIntentStore.RenewManyAsync`, so the store load does not grow with the queue. A batch that fails is logged
  once (`117_134`) and renewed again a third of the grace later.
- **An attempt is counted when a silo takes a command over.** A resumed command's attempt is counted by the renewal
  with which a silo takes its hand-over over (`TryRenewFromAsync`), not by the drain's claim. A hand-over the pool
  refuses, or one that finds its command still held, counts nothing. That covers records that lapsed while their
  commands waited, for example on an exhausted connection pool: the drain hands them over again, and the hand-overs
  are dropped as duplicates. A first hand-over the pool refuses gives back the attempt its dispatch counted, and a stop
  that ends a command gives its attempt back.

A store of your own keeps these promises when it counts the attempt in `TryRenewFromAsync` rather than in the claim,
implements `RenewManyAsync` (its default renews one at a time) and `ReturnAttemptAsync` (its default gives nothing
back). A store that still counts at the claim stays bounded, but a hand-over that only waited counts too. Size the connection pool of the store a silo
uses for its handlers, readers and renewals together ([connection pools](../getting-started/di-composition.md)).

The permits are kept in memory by one activation in the durable directory, and the bound holds across the
loss of its silo. A keeper that is activated — after a failover, and on a cluster's first heavy command,
because it cannot tell the two apart — admits no new unit for one `HeavyWorkOptions.PermitLease`, the
grace: a heavy command dispatched in that window waits, asking again every `PermitRetry`, as it does
when the bound is full. Every unit still running registers with the new keeper at its next renewal, at the
latest half a lease after the keeper is reachable, and counts against the bound again, so when the grace
ends the table is whole. The first heavy commands of a fresh cluster therefore start one lease late —
thirty seconds by default; shorten `PermitLease` if that is too long for the host. A unit that registers
after the grace, when the bound is already full, is one that had stopped renewing for a whole lease; it
keeps running, because a running handler is not paused, is logged as `117_114`, and asks again at every
renewal until a permit is free.
