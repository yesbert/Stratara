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

The runtime's `MessagingOptions.ResponseTimeout` (thirty seconds by default) bounds a caller's wait for one
grain call. On this model that is the wait for **one forwarded command**: a command a handler sends for
another aggregate, or a command dispatched through the mediator that names an aggregate. A handler that
cannot finish inside it is heavy work — mark the command `IHeavyCommand` — or the host sizes the timeout.
The aggregate's own order is not bounded by it: the commands accepted into one aggregate's order run to the
end however long the order takes, because the grain runs its order through a one-way call to itself.
The hand-over of a heavy command or of a command that names no aggregate spans the whole unit, so a unit
longer than the timeout ends that call with a timeout nobody waits for; it is not logged.

## Sends between aggregates

A handler running for one aggregate may send a command for another; the command runs in the other
aggregate's activation, after the command running there, and the sender waits for it. The sends must form
a directed acyclic graph: a send that names an aggregate whose turn is waiting on the sending chain —
A sends to B, and B's handler sends back to A — is refused at once with a message naming both aggregates,
B's command fails with the refusal and A's command with B's failure. Nothing waits for the response timeout.
Where a handler has to reach back, dispatch through `ICommandOutboxDispatcher` instead: the command is
recorded and handed over without waiting, in its own turn.

## Kept commands

A recorded command whose handler keeps failing is resumed up to `MessageRetryOptions.MaxDeliveryAttempts`
times and then kept: it stays in the outbox table with its attempt count and its last failure, and the
commands after it are still resumed. Find the kept commands with:

```sql
SELECT id, aggregate_id, attempt_count, last_failure
FROM outbox_entry
WHERE kept_at IS NOT NULL;
```

Once the cause is fixed, return a kept command; it is resumed with its attempts starting over:

```sql
UPDATE outbox_entry
SET kept_at = NULL, attempt_count = 0
WHERE id = @id AND kept_at IS NOT NULL;
```

A failing handler is seen before the command is kept: every attempt that fails is logged as `117_112`
with the command's identity, its type and the aggregate it names, and a hand-over that fails as `117_113`;
the resumption that follows logs `117_004` with the attempt number, and the keep logs `117_104`.

## A partition that stops advancing

An entry a projection or saga cannot apply stops its partition. The checkpoint stays before the entry,
the failure is logged with the entry's identity (`117_101`, every attempt `117_102`) and counted in
`orleans.reader.stalled`, and the entry is tried again on every wake-up and poll — nothing after it in
that partition advances until it passes. A read that fails before any entry applied — the store
unreachable, a checkpoint the reader refuses — counts as a stall of the same partition and is logged as
`117_103`, whichever wake-up or poll started it; the next one reads again. The event identifiers are
listed in the [log events schema](../reference/log-events-schema.md). A missing prerequisite from another
partition is retried under the preceding-fact policy in the same way.

Under the portable reader a partition also stops at an entry that has **no partition position**: a process
appended it without `PartitionCounterInterceptor`. The logged failure names the entry, the interceptor and
`PartitionCounterBackfill`. Add the interceptor to the write context of that process, then run
`PartitionCounterBackfill.RunAsync` once; the partition continues from where it stopped. The backfill gives the
entry a position after everything the partition had positioned, so a stream whose later versions were appended
with the counter meanwhile is read with the late entry after them; a read model that then stops on a missing
preceding fact is rebuilt. A host that refuses
to start naming a partition counter beyond its partition count was configured with a lower count than the
store was counted with; restore the count.

A checkpoint is advanced only from the position its reader last saw and never under another reader's name. An
activation that outlived its successor — a silo suspected dead that is still writing — is refused with a message
naming both positions, logged as `117_103`, and reads the checkpoint again instead of rewinding it; a write under
another reader's name is refused naming both readers. A host that switches readers resets its checkpoints first.

Under the native reader, a host that lowers its partition count and resets its checkpoints as documented may
still have keep-alive reminders of readers beyond the new count, if the reminders were not reset. Such a reader,
when a reminder brings it back, retires: it unregisters its keep-alive, logs `117_005` naming the consumer, the
partition and the count, reads nothing and counts no stall. The event appears once per retired reader.

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
attempt or hand-over), `117_114` (a heavy unit running outside the cluster-wide bound). `117_008` (a handler
stopped with its silo) explains a second run of a command or a timer after a deploy. Worth routing to a
dashboard rather than an alert: `117_006` and `117_007`, logged once when a full replay starts holding recorded
commands back and once when it releases them — a command that waits for the length of a replay is waiting, not
lost. The whole band is listed in the [log events schema](../reference/log-events-schema.md).

## Reset what the model keeps

`IExecutionModelReset` clears everything the model keeps beside the event stream for the host's
deployment: the reminders of its service, and with them every durable timer; the membership rows of its
cluster; the checkpoints of the projections and sagas it registers; and the grain directory's entries,
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
A checkpoint at the beginning counts as absent and is seeded, so run the reset first where a full re-read is
wanted, and seed afterwards only the consumers that are current.

### Sharing a read store

A checkpoint belongs to a consumer and a partition, not to a deployment. Two deployments can keep their
checkpoints in one read store only when no projection name is registered by both. Every deployment's
store-reading sagas read under one consumer, so at most one deployment sharing a read store runs
`AddStrataraSagaGrains`; two would overwrite each other's positions.

## Reminder profile and clocks

A deployed silo runs Orleans' reminder defaults. Keep-alive periods and the timer retry period are kept
as reminders, so they must be at least the runtime's minimum reminder period; the host fails at start
otherwise. Lowering `ReminderOptions.MinimumReminderPeriod` is for tests that need to observe a reminder
within seconds.

A timer's due time is computed on the host that registered it and its tick runs on a silo with a clock of
its own. A tick earlier than the due time by less than `DurableTimerOptions.DueTolerance` fires instead of
waiting a whole retry period.

A timer fires once however long its handler runs. When a handler outlasts the reminder call's response
timeout, the runtime delivers the next tick while it still runs; that tick does nothing, and the timer is
unregistered once the handler has completed — or stays registered, and fires again on another silo, when the
handler was stopped with its silo. A handler may register its own owner and purpose again, with the due time it
fired for or a later one; the new timer is kept.

An owner id is at most 139 characters and a purpose at most 130: the reminder table holds 150 for each, with the
grain type's name or the due time taking the rest. A longer one is refused by `IDurableTimers` on every member,
with a message naming the limit.

## Stopping a silo

A silo stopped gracefully waits for the handlers running on its grain paths — a command in its aggregate's
activation, a command without an aggregate, heavy work, a timer — for the deactivation budget,
`GrainCollectionOptions.DeactivationTimeout` (30 s by default), a setting the host sizes. A handler that completes
within it is never interrupted. Past it, the `CancellationToken` each handler received is cancelled, and each
stopped handler is logged as `117_008` with what it was running:

| Path | What happens after the stop |
|---|---|
| Recorded command | Its hand-over lapses; another silo resumes it after `OrleansDispatchOptions.IntentGrace`, and no attempt is counted |
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
version check refuses the later one, which is resumed within `MessageRetryOptions.MaxDeliveryAttempts` like
any failing command. Mark a command heavy only where it rarely meets a stream of other commands on its
aggregate.

The pool is as many pools as `HeavyWorkOptions.ClusterWideLimit` needs, eight slots each, placed on silos
of the command role; the permits bound the cluster. A heavy hand-over is leased from the moment the pool
accepts it — while it waits for a slot, while it waits for a permit and while it runs — so a burst that
queues units for longer than `OrleansDispatchOptions.IntentGrace` hands none of them over twice. The
lease and the permit are renewed from timers of their own, off the activation's scheduler, so a handler
that computes without yielding is renewed all the same: however long a handler runs, and whether or not it
yields, it runs once.

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
