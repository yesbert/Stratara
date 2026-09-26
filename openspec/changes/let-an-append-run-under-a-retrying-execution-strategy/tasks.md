## 1. Reproduce first

- [ ] 1.1 `tests/Stratara.Testing.Orleans.Tests`: an append through the framework's unit of work on SQLite with
  an execution strategy that retries and `PartitionCounterInterceptor`; assert it is saved and positioned.
  Confirm it fails on `main` with EF's "does not support user-initiated transactions".

## 2. The fix

- [ ] 2.1 `UnitOfWork<TDbContext>`'s transaction runs its save as a retriable unit when the context is relational,
  its strategy retries on failure and no transaction is current or ambient: transaction begun with the caller's
  token, `SaveChangesAsync(false, token)`, commit with `CancellationToken.None`, `AcceptAllChanges`, savepoints off.
- [ ] 2.2 `tests/Stratara.EntityFrameworkCore.Tests`: the unit under a retrying strategy saves once and commits; a
  transient failure on the first attempt runs the unit again and saves once; a context without a retrying strategy
  still saves without a transaction of the unit of work's own.

## 3. Documentation

- [ ] 3.1 `docs/guides/migrate-to-the-orleans-execution-model.md` (the portable reader): a retrying strategy is
  supported for the framework's appends; a host's own save under one wraps itself in the strategy.
- [ ] 3.2 `CHANGELOG.md` → *Unreleased*.

## 4. Verify

- [ ] 4.1 `openspec validate let-an-append-run-under-a-retrying-execution-strategy --strict`.
- [ ] 4.2 Local gauntlet green; the Orleans and transport integration suites green.
