## Context

`PartitionCounterInterceptor` keeps the partition positions the portable commit-order reader orders by. It
must advance the counter and insert the entries in one transaction, so when a save starts without one it
begins its own in `SavingChangesAsync` and commits it in `SavedChangesAsync`. EF runs `SavingChangesAsync`
before the save enters the context's execution strategy, and a retrying strategy refuses a transaction that
is begun outside it. Evidence: a probe on SQLite with an `ExecutionStrategy` that retries fails every append
with EF's "does not support user-initiated transactions".

The framework's appends all go through `UnitOfWork<TDbContext>`'s transaction: `EventSource` opens one per
save and calls its `SaveChangesAsync`. Since `tell-a-committed-save-from-a-failed-one` that save passes the
caller's token where the context carries `CommitCompletionInterceptor`, and none where it does not.

## Goals / Non-Goals

**Goals:**
- A framework append on a write context whose execution strategy retries on failure is saved and positioned.
- A transient failure during it runs the whole append again — counter, inserts and commit — as one unit.
- Nothing changes for a context whose strategy does not retry.

**Non-Goals:**
- A save a host makes on its own, outside the framework's unit of work, under a retrying strategy with the
  partition counter's interceptor. Such a host wraps the save in the strategy itself, as EF documents; the
  guide says so.
- Detecting that a commit whose acknowledgement was lost did commit (`verifySucceeded`): the unit of work
  does not know what the caller staged, and a retried append that had committed fails as a concurrency
  conflict, which the framework already reports as a retry rather than a failure.

## Decisions

**The unit of work runs the save as a retriable unit when the strategy retries.** When the context is
relational, its execution strategy retries on failure, and no transaction is current or ambient, the
transaction's save runs through `CreateExecutionStrategy().ExecuteAsync`: begin a transaction with the
save's token (the caller's where the context carries `CommitCompletionInterceptor`, none where it does not), `SaveChangesAsync(acceptAllChangesOnSuccess: false)` with the save's token, commit without
the caller's token, then `AcceptAllChanges`. The partition counter's interceptor sees the transaction and
stamps positions inside it; a retry finds the entries still added and stamps them again. Evidence: EF's
documented pattern for a user transaction under a retrying strategy;
`UnitOfWorkRetryingStrategyTests.Under_a_retrying_strategy_a_commit_that_fails_once_runs_the_whole_save_again`
(the changes survive a failed commit) and
`PartitionCounterRetryingStrategyTests.An_append_retried_after_a_failed_commit_is_positioned_afresh` (the counter
is advanced again in the retried unit).
- *Alternative:* the interceptor detects a retrying strategy and does not begin its own transaction, relying
  on EF's. Rejected: EF opens its transaction after `SavingChangesAsync`, so the counter update would run
  outside the transaction that inserts the entries, and a later transaction could commit first.
- *Alternative:* the explicit unit for every relational context. Rejected in round 13 of the previous change:
  it throws under an ambient transaction, and changes when `SavedChanges` runs for every host.

**Savepoints are switched off inside the unit.** A transaction the framework began for one save needs no
savepoint to roll back to; EF would otherwise add one around the save and a round trip to release it.

**The token rule of `tell-a-committed-save-from-a-failed-one` carries over.** Inside the unit, the writes
honour the save's token as they do today — the caller's where the context carries
`CommitCompletionInterceptor`, none where it does not — and the commit never does.

## Risks / Trade-offs

- On every context with a retrying strategy — write, read or projection — a consumer override of
  `SaveChangesAsync(CancellationToken)` alone is not called, because the unit calls the
  `(bool, CancellationToken)` overload EF's pattern needs; `SavedChanges` handlers run before the commit, as in
  any user transaction; and a single-statement save runs in a transaction. The CHANGELOG, the `UnitOfWork`
  remarks and the migration guide say so.
- A host that saves on such a context itself, with the partition counter, must run the same unit; wrapping
  only `SaveChangesAsync` in the strategy loses an append whose commit fails once. The guide shows the unit.
