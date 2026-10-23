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

### D2: The heavy pool refuses beyond `QueuedLimitPerPool`

`HeavyWorkOptions.QueuedLimitPerPool` (default 256, validated to be at least what a pool runs at once, the smaller of
eight and the cluster-wide limit) bounds what one pool activation holds. A hand-over that finds the bound reached is
answered without being accepted and logs `HeavyPoolFull` (Debug, with the pool and the bound). Under D3 a refused
resumed hand-over has counted nothing. A refused first hand-over gives back the attempt its record counted, which is
one statement and only for that case. The record keeps
the stamp of its last hand-over, so it is due again after the grace, and the drain then claims it at most a batch
at a time. The total held is `pools × QueuedLimitPerPool`, which defaults to 256 per eight units of the cluster-wide
limit.

*Why 256:* it keeps the renewal set of a silo far inside one batch, and it holds 32 rounds of a pool's eight
workers. Units beyond that are better off in the table, where they cost nothing.

### D3: A resumed attempt is counted when a silo takes the hand-over over

*Revised after review; the plan read "a duplicate of a held intent gives back its claim's attempt".* The claim no
longer counts. The EF store's `TryClaimAsync` and `ClaimAsync` only stamp, and `TryRenewFromAsync`, the fencing
renewal with which a silo takes a resumed hand-over over, increments the count where it moves the stamp. A
hand-over the pool refuses never reaches it. A duplicate of a held intent is answered before it. Of two hand-overs of
one claim stamped in the same millisecond, only the one that moves the stamp counts. So waiting counts nothing by
construction, and no give-back is needed except for the first hand-over the pool refuses. That one counted at the
record, whose write is free, and the 13 % throughput a renewal at every start had cost rules out moving that count.

*Why the give-back plan was dropped:* the review found that a duplicate's give-back can erase the only counted
attempt of two claims stamped in one millisecond (`ClaimAsync`'s own remark), so a failing handler could run once
past its bound. It also found that every refusal cost a claim plus a give-back per grace, and that five call sites
had to remember the give-back. *Trade-off accepted:* a resumed command whose silo dies after the claim and before the
takeover counts no attempt for that hand-over. It is resumed again after the grace, as a lost message is redelivered.
*Port:* the meaning of `TryClaimAsync`, `ClaimAsync` and `TryRenewFromAsync` is documented anew. A store of your own
that keeps counting at the claim stays bounded, but counts waiting too.

### D5: Review findings on the renewal loop

The loop starts with the first hold and stops when the container disposes it, not when the host stops: the host
stops its services before the silo, which runs handlers through its deactivation budget afterwards. Holds are
counted, so two leases of one intent on one silo keep it renewed until both release it. A timeout inside a batch is
logged and the other batches still run. A give-back that fails is logged (`117_135`).

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
