## 1. Reproduce first

- [x] 1.1 `tests/Stratara.Testing.Orleans.Tests`: an append through the framework's unit of work on SQLite with
  an execution strategy that retries and `PartitionCounterInterceptor`; assert it is saved and positioned.
  Confirm it fails on `main` with EF's "does not support user-initiated transactions".

## 2. The fix

- [x] 2.1 `UnitOfWork<TDbContext>`'s transaction runs its save as a retriable unit when the context is relational,
  its strategy retries on failure and no transaction is current or ambient: transaction begun with the save's
  token, `SaveChangesAsync(false, token)`, commit with `CancellationToken.None`, `AcceptAllChanges`, savepoints off.
- [x] 2.2 `tests/Stratara.EntityFrameworkCore.Tests`: the unit under a retrying strategy saves once and commits; a
  transient failure on the first attempt runs the unit again and saves once, whether the insert or the commit
  fails (`…a_commit_that_fails_once_runs_the_whole_save_again` pins that the changes are accepted only after the
  commit); a context without a retrying strategy still saves without a transaction of the unit of work's own.
  `PartitionCounterRetryingStrategyTests.An_append_retried_after_a_failed_commit_is_positioned_afresh`: the counter
  is advanced again in the retried unit.

## 3. Documentation

- [x] 3.1 `docs/guides/migrate-to-the-orleans-execution-model.md` (the portable reader): a retrying strategy is
  supported for the framework's appends; a host's own save under one runs the full unit, shown in the guide; the
  behaviour a retrying strategy changes for every framework save is listed there, in the CHANGELOG and in the
  `UnitOfWork` remarks.
- [x] 3.2 `CHANGELOG.md` → *Unreleased*.

## 4. Verify

- [x] 4.1 `openspec validate let-an-append-run-under-a-retrying-execution-strategy --strict`.
- [x] 4.2 Local gauntlet green; the Orleans and transport integration suites green.
