> **Status:** approved

# Let a replay survive a passing failure

## Why

A projection replay empties every read model first and then applies the whole event stream in
order. Nothing on that path tolerates a failure: the first exception a projection throws — or the
event store throws while a batch is being read — ends the replay, and the read models stay in
whatever partial state was reached. That is what the `projections` capability says today, and it is
the right shape for a deterministic failure: an event that cannot be applied will not apply on the
second attempt either, and no amount of continuing repairs the view it belongs to.

It is the wrong shape for a *passing* failure. A command timeout on the read store, a dropped
connection, a lock held a moment too long: each of these succeeds a second later, and each of them
ends a replay today with the same finality as a genuine defect. A consumer has had exactly this
happen — a read-store timeout part-way through a rebuild left an environment empty until the replay
was run again by hand. Every other path in the framework that touches a database or a broker runs
under a named resilience policy. The replay is the one that does not.

**What this change deliberately does not do.** The consumer's finding proposed larger answers as
well: rebuilding into a shadow set and swapping at the end, so a failed replay leaves the previous
views in place; or continuing past a failing event, collecting failures, and reporting them at the
end. Both were weighed and both are declined here. The framework does not own the read-model schema,
so a shadow rebuild is consumer work with a framework hook, not a framework feature. Continuing past
a failed event turns "the replay stopped" into "the replay finished with holes", and a hole nobody
can see is what the `projections` capability's own rationale calls the worse outcome. A replay is a
maintenance operation, run in a window, after a backup; the backup is the fallback when a replay
cannot complete, and it belongs to the operator, not to the framework. The owner's decision to that
effect is recorded in `design.md`.

**Why now:** a consumer has just adopted the framework's advice to make projections throw on a
missing row, which enlarges the set of exceptions a replay can meet. The passing kind should not be
among the ones that end it.

## What Changes

- **A replay retries a failing batch before it gives up.** Each batch — the read from the event
  store and the application of its entries — runs under a new named resilience policy: a small,
  bounded number of attempts with exponential, jittered backoff, on the order of half a minute in
  all. A failure that passes within that window no longer ends the replay. A failure that does not
  still ends it exactly as today, with the same failure record and the same partial state.
- **A retried batch is re-applied from its start.** Entries applied before the failure are applied
  again on the next attempt. Projections are already required to converge under a second
  application, because delivery is at-least-once; a replay retry is that guarantee exercised.
- **Each retry is visible.** A warning names the batch, the attempt and the failure, so an operator
  watching a replay sees it struggling rather than merely slow.
- **The set of named policies grows by one**, addressable by a published name like the five that
  exist.
- The documentation of replay states what a retry covers and what it does not, and that a replay is
  a maintenance operation whose fallback is a backup.

The truncation, the ordering, the progress reporting, the failure record and the lease are
unchanged. A replay that fails on a deterministic error behaves as it does today, later by the
length of the retry window.

## Capabilities

### New Capabilities

_none_

### Modified Capabilities

- `projections`: a new requirement that a replay retries a failing batch a bounded number of times
  before it fails, re-applying the batch from its start; the existing *A replay fails partway*
  scenario is unchanged and now describes what happens once the attempts are exhausted.
- `resilience`: *The framework registers named resilience policies* counts six policies rather than
  five; a new requirement describes the replay policy — bounded attempts, backoff on the order of
  half a minute, any failure retried except cancellation.

## Impact

- `src/Stratara.Resilience/Resilience/ResilienceNames.cs` and `ResilienceFactory.cs` — the new
  named policy; `DependencyInjection/ResilienceServiceCollectionExtensions.cs` registers it.
- `src/Stratara.Projections/Services/ProjectionReplayWorker.cs` — each batch runs under the policy
  in a fresh scope per attempt; a new constructor dependency on the pipeline provider.
- `src/Stratara.Diagnostics/LogEvents.cs` and
  `src/Stratara.Projections/Diagnostics/Extensions/LoggerProjectionExtensions.cs` — one new warning
  event for a retried batch.
- `docs/guides/write-a-projection.md` — the *Replay is destructive* section gains the retry and the
  maintenance-window statement.
- `CHANGELOG.md` — `[Unreleased]`.
- Additive on every published surface: a patch release.
- Source: consumer finding F-008, captured 2026-09-02; the three larger options it lists are
  declined here, with the reasoning in `design.md`.
