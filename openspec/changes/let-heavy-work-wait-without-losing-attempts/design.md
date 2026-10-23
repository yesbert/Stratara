## Context

See `proposal.md`, *Why*. Read on `main` at `065d14b` (`v4.4.6`) plus `return-a-long-hand-over-once-it-is-accepted`:

- **A lease is a timer per command.** `IntentLease.StartAsync` creates a timer per lease at a third of `IntentGrace`.
  Each tick issues `ICommandIntentStore.RenewAsync(intentId, now)`, one `UPDATE` in the EF store
  (`CommandIntentStore.RenewAsync`). A failed tick is logged (`LogIntentRenewalFailed`) and skipped.
- **Attempts are counted at the claim.** `TryClaimAsync` and `ClaimAsync` stamp the hand-over and increment
  `attempt_count`. `RecordAsync` with `recordedAt` counts the dispatch's own hand-over as the first attempt.
  `ReturnAttemptAsync` gives one back, never below zero. The resumer keeps a command whose count reached
  `MaxDeliveryAttempts`.
- **A duplicate is dropped silently.** `HeavyWorkGrain` (`_held`), `AggregateGrain` (`_heldIntents`) and, after the
  previous change, `CommandRunnerGrain` (`_held`) answer a hand-over of an intent they hold without running it. The
  claim that produced it has already counted an attempt.
- **The heavy pool accepts without bound.** `HeavyWorkGrain` queues every hand-over on the runner's channel. The
  consumer observed 11,372 held at once.
- **A stop gives attempts back** once the runner no longer blocks the host's stop
  (`return-a-long-hand-over-once-it-is-accepted`, D5). A queued unit's cancellation runs it into
  `CommandExecution.RunIntentAsync`'s stopping branch, which calls `ReturnAttemptAsync`.
- **Precedent for batching:** `IntentCompletionQueue` collects completions and deletes them in batches from a
  hosted loop.

## Goals / Non-Goals

**Goals:** waiting never costs an attempt; the store load of holding commands does not grow with how many are held;
a burst becomes a backlog in the table.

**Non-Goals:**

- Changing when the claim counts. The claim's count is what bounds a command that is lost on the way, such as a
  hand-over to a silo that dies. Giving it back in the three cases where nothing ran keeps that bound and removes
  the cost of waiting, without changing the port's semantics for other implementations.
- Pausing the drain while pools are full. A refused hand-over gives its attempt back and stays due only after the
  grace, so the drain's pace is already bounded by the refusals. A drain-level signal can follow if measured load
  asks for it.

## Decisions

### D1: One renewal loop per silo, batched through the port

`IntentRenewals`, a singleton hosted service in the silo, holds the set of held intent ids. `IntentLease.StartAsync`
registers its id, and `DisposeAsync` unregisters it. The lease no longer owns a timer. The loop ticks every third of
the grace, takes a snapshot, and calls `RenewManyAsync(ids, now)` in chunks of `RenewalBatchSize` (internal, 1,000).
A chunk that fails is logged once (new event `IntentBatchRenewalFailed`, Warning, with the count) and the next tick
tries again. One failed renewal still does not let a hand-over lapse, as the grace spans three ticks.

`ICommandIntentStore.RenewManyAsync(IReadOnlyList<Guid> intentIds, DateTimeOffset now, CancellationToken)` defaults
to a loop over `RenewAsync`, so a host-supplied store keeps working at its old cost. The EF store overrides it with
`ExecuteUpdateAsync` over `intentIds.Contains(e.Id)`, which is one statement per chunk.

*Why a silo-wide loop rather than a pool-wide one:* aggregate orders and runners hold leases too. The finding is
about heavy work, but the cost model is the same for every held command, and one loop serves all of them.

*Alternative rejected: renew only commands whose handler is running and park waiting ones with a far-future stamp.*
A silo that dies would then strand its waiting commands until that stamp, instead of the grace.

### D2: The heavy pool refuses beyond `QueuedLimitPerPool`, and the refusal gives the attempt back

`HeavyWorkOptions.QueuedLimitPerPool` (default 256, validated positive) bounds `_inFlight.Count` per pool
activation. A hand-over that finds the bound reached is answered without being accepted. Before answering, it calls
`ReturnAttemptAsync` for the intent and logs `HeavyPoolFull` (Debug, with the pool and the bound). The record keeps
the stamp of its last hand-over, so it is due again after the grace, and the drain then claims it at most a batch
at a time. The total held is `pools × QueuedLimitPerPool`, which defaults to 256 per eight units of the cluster-wide
limit.

*Why 256:* it keeps the renewal set of a silo far inside one batch, and it holds 32 rounds of a pool's eight
workers. Units beyond that are better off in the table, where they cost nothing.

### D3: A duplicate of a held intent gives back its claim's attempt

Where a grain answers a hand-over of an intent it already holds (D-Context), and the hand-over carries a claim stamp
(`envelope.ClaimedAt`, set only by the drain's resumption), it calls `ReturnAttemptAsync` before answering. A first
dispatch carries no stamp and is never a duplicate of itself. A helper on `IntentLease` (`GiveBackAsync(services,
intentId)`) serves all three grains.

### D4: As built

- `IntentRenewals` is registered by `AddStrataraAggregateGrains`, the registration of the role that holds leases, and
  is a hosted service like `IntentCompletionQueue`. A lease built without it (unit tests, host-free use of
  `IntentLease`) keeps its own timer, so nothing that built leases before breaks.
- A lease's `DisposeAsync` releases its command at once. A batch already sent may renew it one more time, which can
  delay the resumption of a command whose attempt failed by at most one renewal period. Accepted.
- The pool counts `_held`, which includes units still waiting for a worker or a permit, so the bound is on
  everything the activation holds.
- Evidence: the burst tests at this scale also pass on the previous code. The lapse test reproduces the finding's
  chain deterministically, with renewals failed by a store decorator, and it fails on the previous code with a
  record at attempt three.

## Risks / Trade-offs

- **[A refused command waits a full grace before it is tried again]** → That is the intent: it waits in the table,
  costing nothing, instead of in memory. The grace is the host's setting, and the guide says so.
- **[A store that does not implement `ReturnAttemptAsync` counts refusals]** → Its default already gives nothing
  back for stops. The guide names both members as required for a store under bursts.
- **[`Contains` over 1,000 ids]** → Npgsql sends it as `= ANY(@array)`, a single parameter.

## Migration Plan

No schema change. Silos of this release renew in batches. Silos of the previous one renew per lease. Both write the
same column. A pool of the previous release does not refuse, so a mixed cluster bounds only the silos already
upgraded.
