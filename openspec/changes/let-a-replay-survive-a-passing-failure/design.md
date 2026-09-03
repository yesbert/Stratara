# Design — Let a replay survive a passing failure

## Context

See `proposal.md` → *Why*. What matters here is the shape of the replay loop and of the policies
that exist.

`ProjectionReplayWorker.ReplayEventsAsync` (`src/Stratara.Projections/Services/ProjectionReplayWorker.cs:96`)
loops over batches. Per batch it opens one DI scope, starts a write-side transaction to read
`BatchSize` entries after the last sequence number (`5000` by default), and for each entry sets the
recorded session context and calls `IProjectionManager.HandleAsync` with that one entry's events.
After the batch it advances `afterSequence`, reports progress — which is also what renews the lease
on the active marking — and logs `ProjectionReplayBatchPublished`. Nothing on the path catches; the
first exception reaches the outer `catch (Exception)` in `ExecuteAsync`, which records `SetFailed`
and, via `finally`, deactivates.

`ProjectionManager.HandleAsync` fans out over projections with `Parallel.ForEachAsync` and lets
whatever a projection throws propagate. The projections commit their own read-store writes; the
replay's transaction is on the event store and covers only the read.

Five named policies exist in `Stratara.Resilience`, all built in `ResilienceFactory`. The two
dispatcher policies have the shape a replay needs — bounded attempts, exponential backoff with
jitter, any exception — but with three attempts from 200 ms they are tuned for a broker accepting a
message, not a read store recovering from a timeout. The message-bus policy retries forever behind a
breaker, which is the wrong shape for a replay: a deterministic failure would never end it. The
build guidance forbids a hand-rolled retry loop; a new retry is a new named policy, registered by
`AddResiliencePipelines`, which every worker host already calls.

The replay worker is constructed by the host's container with four dependencies. Its test harness
builds it by hand (`ProjectionReplayWorkerTests.Harness.RunAsync`).

## Goals / Non-Goals

**Goals:**

- A batch that fails and then succeeds within the window does not end the replay, on the read and
  on the apply side alike.
- A batch that fails on every attempt ends the replay exactly as any failure does today.
- A retry re-runs nothing outside the batch and leaves no state behind from the failed attempt.
- The operator can see a retry happening.

**Non-Goals:**

- Surviving a deterministic failure. The owner's decision is below.
- Preserving the previous read models across a failed replay. Same decision.
- Changing the unit of progress or of the lease. Progress is still reported per completed batch.
- Making the attempt count or the backoff configurable. The five existing policies are constants
  in the factory, and a consumer that wants a different shape can register a pipeline under the
  same name before the framework does; this policy follows suit.

## Decisions

### Owner decision, 2026-09-03: retry, and nothing larger

The consumer's finding (F-008) proposed three shapes: rebuild into a shadow set and swap at the
end; keep truncation but continue past a failing event, collecting failures and reporting them at
the end; or at minimum honour the missing-prerequisite report on the replay path by deferring the
event and retrying it after the batch.

The owner declined all three and asked for a bounded retry only. The reasoning, recorded here so
the next reader of F-008 finds the decision rather than the menu:

- A replay is a maintenance operation. It runs in a window, after a backup. If it fails and does not
  complete on a second or third attempt, the operator restores the backup and looks for the cause.
  That procedure belongs to the product that uses the framework, not to the framework.
- The shadow rebuild is the only shape that delivers the guarantee the finding asked for — the
  previous views survive a failed replay — and it is mostly consumer work: the framework does not
  own the read-model schema, and `IProjectionViewTruncator` is a one-method consumer interface with
  no implementer in this repository. A hook without an implementation would be a promise nobody
  keeps.
- Continuing past a failed event produces a read model with a hole and a report saying so. The
  `projections` capability's own rationale for *A failing projection stops the bundle* names that
  outcome as the worse one, because a hole is invisible to everyone who did not read the report.
- Deferring a missing-prerequisite report during a replay retries something that cannot succeed. In
  sequence order the prerequisite is by construction already applied, so the report means the view
  never had the row — a defect, not a race — and waiting changes nothing.

What a retry *does* cover is the failure the consumer actually observed: a read-store timeout
mid-rebuild. That is worth the half day it costs.

Evidence: this conversation; the finding's own text, which states that in a replay the
missing-prerequisite report "can only mean the view genuinely never had the row".

### The batch is the unit of retry, in a fresh scope per attempt

The policy wraps the whole of one batch iteration: create scope, read entries, apply each, and
return the last sequence number. A failed attempt disposes its scope; the next attempt starts from
the same `afterSequence` with a new one.

*Rejected: retry per entry, inside the batch's scope.* Finer, and unsafe. The scope holds the
read-store contexts the projections write through; after a failed save an EF Core context may hold
tracked state from the failed attempt, and a timeout may leave a connection whose transaction is in
an unknown state. Retrying inside that scope retries against whatever the failure left behind. A
fresh scope is the only attempt that starts clean.

*Rejected: retry per entry in a fresh scope each.* Correct, and it turns one scope per five thousand
entries into five thousand. The replay is measured in hundreds of events per second on a consumer's
data; a scope per event would be visible.

The cost of the chosen unit is re-applying up to `BatchSize` entries that had already succeeded
before the failure. That cost is paid for by a guarantee projections already give: at-least-once
delivery on the bus already requires a second application to converge, and the projection guide
says so in the same breath as it mentions replay. A projection that accumulates rather than
converges is wrong on the bus today; the retry does not make it more wrong.

Evidence: `ProjectionReplayWorkerTests`, which will pin that a batch failing once is read again from
the same sequence number and its entries are applied again.

### A sixth named policy, shaped like the dispatch policies but slower

`ResilienceNames.ProjectionReplayBatch`, built by `ResilienceFactory.CreateProjectionReplayBatchPipeline`:
five attempts, exponential from one second with jitter, roughly thirty seconds in all, retrying
any exception. Polly's default predicate already excludes `OperationCanceledException`, which is
what makes host shutdown during a retry surface as cancellation rather than as a failed replay.

*Rejected: reuse the event-bundle-dispatch policy.* Right shape, wrong scale: three attempts inside
about a second and a half do not outlast a read-store timeout, which is the observed case.

*Rejected: the message-bus policy.* Indefinite retry behind a breaker. A deterministic failure would
keep a replay alive, and the operator, forever.

*Rejected: a retry only on a classified set of transient exceptions.* The framework does not see the
consumer's read-store provider, so it cannot classify its exceptions. Any-exception with a bound is
what the dispatch policies already do; five attempts is a bound.

Evidence: `ResilienceFactoryTests` for the shape; the resilience spec delta for the count.

### Progress and the lease are untouched; the retry is logged

Progress is reported after a batch completes, as today; a batch under retry reports nothing until it
succeeds. The lease default of `300` seconds comfortably outlasts a thirty-second retry window plus a
slow batch, so the marking does not lapse mid-retry. A consumer who has lowered the lease below the
retry window would already have set it below a slow batch, which the guide warns against.

Each attempt after the first logs a warning — new id `104_011`, `ProjectionReplayBatchRetried` —
carrying the sequence number the batch starts after, the attempt number and the exception. An
operator watching a replay sees it struggling rather than merely slow.

## Risks / Trade-offs

- [A deterministic failure now ends the replay about thirty seconds later than before] → Accepted.
  The failure record and the final state are identical; only the ending is delayed, and the retry
  warnings in between say why.
- [A projection that does not converge under a second application double-counts during a retry] →
  Such a projection is already wrong under at-least-once delivery on the bus. The guide's
  *converge, do not accumulate* table is the existing contract; the changelog entry points at it.
- [A batch that fails at entry four thousand of five thousand re-applies four thousand entries] →
  Bounded by `BatchSize`, which the consumer sets. A consumer that finds this expensive lowers the
  batch size; the trade is theirs.
- [A read-store outage longer than the window] → The replay fails, as it does today. The backup is
  the fallback, per the owner's decision above.
