## Context

The defects and the owner's decisions of 2026-09-16 (placement by role, cycles detected) are in
`proposal.md`. The current code:

- **Queue runner.** `src/Stratara.Orleans/Aggregates/AggregateGrain.cs:139-144` starts the queue with
  `this.AsReference<IAggregateGrain>().RunAcceptedAsync()` and a `ContinueWith(OnlyOnFaulted)` into
  `OnRunNotDeliveredAsync` (`:160-164`), which clears `_running` and calls `AbandonAsync` (`:170-193`).
  `IAggregateGrain.RunAcceptedAsync` (`:33-35`) carries no `[OneWay]`; nothing in `src/` sets
  `MessagingOptions.ResponseTimeout` (30 s default). The forwarding behaviour awaits the forwarded call
  (`AggregateGrainBehavior.cs:43-44`), and `ExecuteAsync` is `[AlwaysInterleave]` (`:19`).
- **Turn marker.** `AggregateTurn` (`AggregateGrain.cs:336-355`) is an `AsyncLocal<Guid?>` holding one
  aggregate id, read by the behaviour to let a command already inside its aggregate's turn pass. It does
  not cross a grain call; Orleans `RequestContext` does.
- **Renewals.** `IntentLease.RenewUntilStoppedAsync` (`IntentLease.cs:99-106`) and
  `HeavyWorkGrain.RenewWhileRunningAsync` (`HeavyWorkGrain.cs:135-160`) are started from inside the
  grain turn; their continuations run on the activation's `TaskScheduler`. `HeavyWorkGrain` is
  `[StatelessWorker(8)]`; `ExecuteIntentAsync` (`:95-96`) calls `CommandExecution.RunAsync`, which
  starts the lease (`AggregateGrain.cs:242`) after the request was dequeued, and holds the call through
  the permit wait (`:104-107`).
- **Failure paths.** `AggregateGrain.cs:111-114` catches and sets the completion; `RunIntentAsync`
  (`:261-265`) records and rethrows; the dispatcher (`OrleansCommandDispatcher.cs:50`) and the resumer
  (`:142`) `.Ignore()` the hand-over task. `OrleansLog.cs` has no event for a failed attempt.
- **Rebuild.** `ProjectionRebuilder.RebuildAsync` (`ProjectionRebuilder.cs:21-51`) pauses every partition
  grain, resets checkpoints, truncates, and resumes in `finally`; `ProjectionGrain._paused` is a `bool`
  (`ProjectionGrain.cs:93-112`); `ReplayCheckpointReset` wraps the truncator the same way; the
  replay state is `IProjectionReplayState.IsReplayActive`.
- **Placement.** `SingletonWorkPlacement` (`Singleton/SingletonWorkPlacement.cs`) publishes one metadata
  key per registered singleton work when the silo builds its metadata, registers a
  `PlacementFilterStrategy` + director, and `SingletonWorkGrain` carries the filter attribute. No other
  grain does; the registrations publish nothing.
- **Reset.** `ExecutionModelReset.cs:52-60` probes `to_regclass(@table)` with unqualified names and
  returns 0 on `null`; `AddStrataraExecutionModelReset` registers the port scoped
  (`OrleansResetServiceCollectionExtensions.cs:58`) and its example resolves from the root (`:44`).
- **Loop.** `StoreReaderLoop.CatchUpAsync` (`StoreReaderLoop.cs:157-212`) reaches `MarkStalled` only from
  `ApplyEachAsync`; a faulted read propagates to the caller, which logs only on the nudge path
  (`StoreReaderGrain.cs:76-89`); the partial-batch checkpoint is written with the loop's token (`:188-198`).

## Goals / Non-Goals

**Goals:**
- No promise of the `orleans-execution` specification depends on a handler's duration, on whether it
  yields, on how many silos share a cluster or on which roles each registered.
- Every failure the execution model swallows today is logged under the Orleans band.
- The migration guide, read alone, composes a host that compiles and starts.

**Non-Goals:**
- Starting readers at the head of a populated read store, and the transaction-id column's migration
  on a populated event table (the next change).
- A deduplicating heavy pool that survives a duplicate hand-over: the fix is to make the duplicate
  impossible (lease at acceptance), not to tolerate it.
- Permit accounting after the permit grain's own silo dies (round-5 finding, medium; its own change).
- Placement per handler, projection name or saga type. Placement is per role; a silo that registers a
  role registers all of that role's handlers, projections or processes, as the guide will say.

## Decisions

### D1 — The queue runner is a one-way call; the abandon path stays for the case it was written for

`RunAcceptedAsync` gets `[OneWay]`. A one-way request carries no response and therefore no response
timeout; the runtime still delivers it into the activation's turn, which is what the self-call is for
(the loop runs as a turn, not as a background task, so it serialises with other requests). The
`ContinueWith(OnlyOnFaulted)` continuation stays: a one-way call still faults locally when it cannot
be sent at all (the activation is shutting down), which is the case `OnRunNotDeliveredAsync` was
written for. `AbandonAsync` is otherwise reached from `OnDeactivateAsync` only.

*Rejected: running the loop as a grain timer or a detached task on the activation scheduler.* Both
run outside a request; a grain timer with `Interleave = false` would serialise, but `RegisterGrainTimer`
from inside `Accept` (called from an interleaving request) adds a timer lifecycle to manage for one
call. `[OneWay]` is the runtime's own answer to "invoke without waiting".
*Rejected: raising the response timeout.* It is the host's setting and bounds every call.

The caller's side — a forwarded command whose single handler outlasts the response timeout — is left
to the timeout by design: the operations guide names `MessagingOptions.ResponseTimeout` as the bound
on one forwarded command and says a handler must fit it or be heavy.

Evidence: Orleans `OneWayAttribute` documentation ("the caller does not wait for a response");
`AggregateGrain.cs:139-164`; the scenario *An aggregate's order takes longer than the response timeout*
will be an integration test with `ResponseTimeout = 2 s` and a queue of five one-second commands.

### D2 — The chain of aggregate turns travels in the request context; a send into its own chain is refused at the sender

`AggregateTurn` grows from one id to a chain: the ids of the turns the ambient flow is inside, innermost
last. Entering a turn pushes; leaving pops. The chain is written to Orleans `RequestContext` under one
key when the behaviour forwards a command (`AggregateGrainBehavior`), and the aggregate grain reads it
on `ExecuteAsync`/`AcceptIntentAsync` and seeds the turn it enters with the caller's chain plus its own
id. Before forwarding, the behaviour checks the chain: a target already in it is a cycle, and the
behaviour throws `InvalidOperationException` naming the sending aggregate (the innermost) and the
target. The failure propagates like any handler failure: B's command fails with the refusal, A's
command fails with B's failure, both callers see it at once.

The chain is an ordered list of `Guid`s; a command that names no aggregate (`AggregateId` null) adds
nothing. A recorded intent resumed by the drain starts a fresh chain — it has no caller. A heavy
command starts its chain with its own aggregate id, so a heavy handler's sends are checked too.

*Rejected: reentrancy (`[Reentrant]`, call-chain reentrancy).* The wait is in the framework's own
queue (A's runner awaits B while A's `ExecuteAsync` already interleaved and queued), so the runtime's
reentrancy cannot release it; only running B's send-back inline would, and that breaks the one-writer
promise A holds.
*Rejected: detecting at the receiver.* The receiver can see the chain too, but failing at the sender
saves the grain call and gives the sender the message.

Evidence: `AggregateGrainBehavior.cs:35-44`; `AggregateGrain.cs:291,336-355`; Orleans `RequestContext`
flows with every grain call. Test: an A→B→A cycle on the PostgreSQL store, asserting the message and
that both calls return well under the response timeout.

### D3 — Renewals run off the activation scheduler; the heavy grain accepts, then runs

Both renewal loops are started with `Task.Run` so their `PeriodicTimer` continuations run on the
thread pool. They touch no grain state: the intent lease renews through a store call on its own
scope, the permit renewal calls the permit grain through a grain reference, which is legal from any
thread. Stopping stays as it is (cancel, await).

`HeavyWorkGrain.ExecuteIntentAsync` becomes `[AlwaysInterleave]` and does what the aggregate grain's
`AcceptIntentAsync` does: start the lease, enqueue the unit into a per-activation queue, and return
once the lease is renewing. A per-activation worker loop — one activation, `MaxLocalWorkers` slots as
today's stateless-worker count, now a `SemaphoreSlim` — takes units in order, acquires the permit,
runs, releases. `[StatelessWorker]` is dropped: one activation per silo holds the queue (the grain is
keyed by silo through the dispatcher, as the hand-over already targets the local pool), and the
runtime's request queue no longer holds unleased hand-overs. The lease starts before the wait; a
crash between acceptance and run leaves a leased record that lapses and is resumed, as today.

*Rejected: keeping `[StatelessWorker]` and leasing in the resumer before the call.* The resumer
cannot renew on the worker's behalf once the call is queued, and a lease held by the caller's silo
dies with it.
*Rejected: `DropExpiredMessages` as the guard.* It drops queued requests only after the response
timeout and only when it exceeds the grace by chance.

Evidence: `HeavyWorkGrain.cs:73-160`; `AggregateGrain.cs:76-88` (accept-then-run pattern);
`HeavyBurstTests.cs` (500 units, limit 4 — will assert one execution per unit).

### D4 — Two log events, at the points the failure is known

`LogEvents.Orleans.IntentAttemptFailed = 117_112` (warning: intent id, aggregate id, command type,
attempt, exception) is written in `CommandExecution.RunIntentAsync` next to `RecordFailureAsync`,
where the attempt number is at hand from the lease. `LogEvents.Orleans.HandOverFailed = 117_113`
(warning: intent id, aggregate id, heavy, exception) replaces the `.Ignore()` in the dispatcher and
the resumer with an observed continuation that logs. `IntentFailure.Describe` keeps type and message
(the column is 2048 characters); the stack goes to the log.

Evidence: `OrleansLog.cs`, `LogEvents.cs:275-307`, `OrleansCommandDispatcher.cs:50,142`.

### D5 — The pause is a count; a replay refuses a rebuild

`ProjectionGrain._paused` becomes an `int`; `PauseAsync` increments and `ResumeAsync` decrements, the
grain is suspended while the count is positive, and `ResumeAsync` nudges only when the count reaches
zero. Both the rebuilder and the replay truncator pause and resume in pairs, so two overlapping
callers hold the readers until the last resumes. `ProjectionRebuilder.RebuildAsync` first checks
`IProjectionReplayState.IsReplayActive` and throws `InvalidOperationException` naming the replay.

*Rejected: a rebuild lease per projection (one rebuild at a time).* It serialises rebuilds but not a
rebuild beside a replay, and it needs a place to keep the lease across silos; the count is local to
the grain that is paused, which is the thing being protected.
*Rejected: a pause token the resumer must present.* Same protection, more surface.

Evidence: `ProjectionGrain.cs:93-112`; `ProjectionRebuilder.cs:27-51`; `ReplayCheckpointReset.cs:37-50`;
unit test on the pause count and an integration test with two concurrent rebuilds asserting the read
model is complete after catch-up.

### D6 — Placement by role, through the mechanism singleton work already uses

`SingletonWorkPlacement` is generalised into a role placement: one metadata key per role
(`stratara.role.aggregates`, `.projections`, `.sagas`, `.timers`, `.heavy-work`), published by the
registration that adopts the role (`AddStrataraAggregateGrains`, `AddStrataraProjectionGrains`,
`AddStrataraSagaGrains`, `AddStrataraDurableTimers`, `ConfigureStrataraHeavyWork` or the heavy
pool's registration), read into the silo metadata at the same point singleton work is. One
`PlacementFilterStrategy` per role, one director that filters silos on the role's key; `AggregateGrain`
and the command-runner grain carry the aggregate filter, `ProjectionGrain` the projection filter,
`SagaGrain` and `SagaProcessGrain` the saga filter, `TimerOwnerGrain` the timer filter, `HeavyWorkGrain`
and the permit grain the heavy filter. `AddStrataraOrleans` registers every filter, as it registers the
singleton filter today, because a grain class naming a filter cannot be placed from a silo without it.

When the filter leaves no silo, the runtime throws at placement; the director wraps that with a message
naming the role and the registration that adopts it, so the caller reads *no silo of the cluster
registered the projection role — call AddStrataraProjectionGrains on one* rather than an empty
placement error. A silo whose metadata is unavailable (the cache not yet warm, the silo joined
before metadata was published) is treated as not publishing, which keeps the guarantee and delays
placement rather than misplacing.

An Orleans client publishes no metadata and hosts no grains; a client with the dispatcher forwards
through `IGrainFactory`, which the client provides. The command-role filter places the aggregate on a
silo publishing the role. A test composes one client host with the dispatcher and one silo with the
command role.

*Rejected: a homogeneity rule with a start check.* Owner decision 2026-09-16; it turns the guide's
role table into a warning and stops a projection host from being scaled apart from a command host.
*Rejected: placement per projection name or handler type.* Finer than the guide's roles, and a name
set in metadata changes with every deploy; roles are stable.

Evidence: `SingletonWorkPlacement.cs:25-97` (the working pattern); `SingletonPlacementTests.cs`;
Orleans placement filters (`AddPlacementFilter`, `PlacementFilterStrategy`, `ISiloMetadataCache`).

### D7 — The reset names its schema and fails on an absent table

`AddStrataraExecutionModelReset` gains an optional `schema` parameter (default `public`); the reset
probes `to_regclass` with the schema-qualified name and throws `InvalidOperationException` naming the
table when it is absent. The three runtime deletes run in one transaction on the runtime connection;
the checkpoint delete and the directory callback follow, and the documentation says a failure after
the runtime deletes leaves checkpoints and directory in place. The port stays scoped; the example and
the guide resolve it from `CreateAsyncScope()`, as the integration test does.

*Rejected: a singleton reset over `IServiceScopeFactory`.* Equivalent, but it changes the port's
lifetime for every host that already resolves it correctly.
*Rejected: probing `information_schema` for any table of that name in any schema.* Guessing the
schema is how a reset removes another deployment's rows.

Evidence: `ExecutionModelReset.cs:25-72`; `ResetTests.cs:59`; the guide's example.

### D8 — A failed read is a stall, and the partial checkpoint is written with an uncancelled token

`StoreReaderLoop.CatchUpAsync` wraps the read and the checkpoint fetch: an exception marks the stall
(counter and lag as for a failing entry), logs `CatchUpFaulted` with consumer and partition, and
rethrows; the grain's poll and reminder paths already propagate, so the log is written once, in the
loop. The partial-batch checkpoint write uses `CancellationToken.None`: the entries are applied, and
the write is the cheaper of the two outcomes for the successor.

Evidence: `StoreReaderLoop.cs:157-212, 243-245`; `StoreReaderCancellationTests.cs`.

### D9 — Documentation is corrected against the code, not restated

Each documentation item in the proposal is a correction of a sentence that contradicts the code or
omits a step the code requires. The reference page lists 117_001–117_113 with the level and the
fields; the operations guide names the instruments and what to alert on (a stall counter above zero,
lag above the host's own bound, a kept command, permits at the limit, 117_111); the migration guide's
package list, script location, retirement trigger, saga row, client option and response-timeout note
are edited in place. `observe-the-framework.md` and `write-a-projection.md` say where checkpoints live
when the execution model reads the store.

## Risks / Trade-offs

- [A one-way runner that is never delivered leaves `_running` true] → the local fault of an
  undeliverable one-way call still reaches the continuation; and deactivation abandons the queue.
- [The chain in the request context grows with deep send trees] → a chain is as long as the nesting
  of sends, a handful of ids; it is not persisted.
- [Renewal on the thread pool races the grain turn] → the loops share nothing with the turn but the
  cancellation token; the lease's stop awaits the loop before the scope is disposed.
- [Dropping `[StatelessWorker]` changes the heavy pool's activation shape] → one activation per silo
  with the same bound; the permit grain and the cluster-wide bound are untouched; HeavyBurstTests and
  HeavyConflictTests cover throughput and conflict.
- [A role's metadata key is missing on a silo that registered the role before this change] → the key
  is published by the registration, so every silo on this version publishes; a mixed-version cluster
  during a rolling upgrade places on the upgraded silos only, which is safe and temporary.
- [A cluster where the only silo of a role is down] → calls for that role fail naming it until the
  silo is back; before this change they activated on the wrong silo and failed there.
- [The reset now throws where it returned zero] → a host that ran the reset against a database without
  runtime tables (nothing to reset) must name a schema that has them, or handle the exception.

## Migration Plan

Patch release. No schema change. New public surface: the `schema` parameter on
`AddStrataraExecutionModelReset`, two log event ids. A consumer whose handlers send in a cycle sees
the refusal at once where it saw a timeout. A cluster of mixed versions during rollout places each
role on the upgraded silos, so upgrade the silos that carry a role before the hosts that call it.
Rollback: the metadata keys are ignored by 4.1.1 silos.
