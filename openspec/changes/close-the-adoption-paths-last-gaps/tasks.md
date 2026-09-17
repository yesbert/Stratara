# Tasks

## 1. The backfills

- [x] 1.1 The transaction-id backfill reads each batch's upper bound and stamps a key range.
- [x] 1.2 The partition counter's backfill takes one batch per transaction, holding the counter lock
      for a batch instead of a partition.
- [x] 1.3 The existing migration and backfill integration tests still pass.

## 2. The head

- [x] 2.1 `ICommittedPositionReader.HeadAsync` says that a head is taken while nothing appends, and
      what a head taken under an open writer costs.
- [x] 2.2 Integration test: a head taken while a write transaction is open does not skip what that
      transaction commits.
- [x] 2.3 The migration guide says it where it tells a deployment to seed.

## 3. Singleton work

- [x] 3.1 A run that fails with a cancellation the work was not asked for is logged.
- [x] 3.2 Two works under one name are refused at registration, naming both.
- [x] 3.3 Unit test for the refusal.

## 4. The spec and the run

- [x] 4.1 The singleton requirement says the overlap is bounded only while the declared silo can read
      the membership; the directory scenario loses the clause the scenario after it already makes.
- [x] 4.2 `CHANGELOG.md`.
- [x] 4.3 Gauntlet and the commit-order integration suites.
