# Align the execution model's specification with its tests

> **Status:** approved (owner, 2026-09-17 — recorded at the owner's request)

## Why

Several scenarios of the `orleans-execution` and `event-sourcing-store` specifications end with a
"verified …" clause that the suite does not verify as written, and several guarantees the
requirements state have no test that could see them broken. A specification whose evidence clauses
are not true of the suite cannot be read as evidence. Closes R5-Cmd-012 and R5-Rdr-011, both re-read
against `main` after #107 and #109, which closed parts of them:

- **"Verified with twenty kills"** stands on *The host dies after acceptance*, and
  `DurableIntentTests.Kills` is 5 (`tests/Stratara.Orleans.IntegrationTests/Aggregates/DurableIntentTests.cs:17`).
  The projections' kill scenario says twenty and `CommitPublishKillTests.Kills` is 20; the timers say
  ten and `HardKillTimerTests.Kills` is 10. One of three is wrong.
- **"The cluster is unstable and activates an aggregate twice"** has no test. The guarantee rests on
  the store's version constraint, which the store's own tests cover, but no test runs two activations
  of one aggregate against one store and shows which writer is refused.
- **"A handler runs longer than the grace"** is verified with an awaiting handler on both paths
  (`DurableIntentTests.cs:51-66`, `Task.Delay`); the requirement says "whether or not the handler
  yields". #107 added `HeavyNonYieldingTests` for the heavy path (`Thread.Sleep` under a two-second
  grace); the aggregate path's non-yielding case has only the unit test on the lease's renewal timer
  (`IntentLeaseRenewalTests`). The send-cycle test the tracker lists as missing exists since #107
  (`SendCycleTests`).
- **`CommittedBatch.ResumePositionBefore` with entries sharing a position** — one transaction, two
  entries, the second fails — is exercised by no test (`src/Stratara.Abstractions/Abstractions/CommitOrder/ICommittedPositionReader.cs:99-115`,
  `StoreReaderLoop.cs:214-220`); every scripted reader in `tests/Stratara.Orleans.Tests` hands out
  distinct positions. The requirement *A failing entry stops its partition* promises the checkpoint
  never advances past the failing entry; with a shared position that means resuming below the group and
  re-applying its applied entries, and nothing shows it.
- **The native reader's whole-transaction read** (`PostgresTransactionIdReader.cs:78-96,115-124`) has
  three cuts — the overflow is another transaction; the last complete transaction continues into the
  overflow and is held back; one transaction is larger than the batch and is returned whole. Since
  #109 `TransactionIdMigrationTests` reads with a batch of 3 against backfill transactions of up to 10
  entries and so passes through the third cut, but asserts only order and the backfill bound; no test
  names the cuts, and the specification's "a batch SHALL never end in the middle of one transaction's
  entries" has no scenario that shows a batch larger than the batch size.
- **The twenty-kill projection test checks presence only** (`CommitPublishKillTests.cs:77-80`, `view`
  returns *present* or *absent*), and the view it reads is version-guarded, so a double application
  would not be seen. The PoC read store also has an unguarded running total
  (`CounterTotalsProjection`) that the test does not read. The kill in that test falls after the commit
  and before any read, so on the checkpoint path a fact is applied exactly once there, and the test
  could say so.

## What Changes

Per item, either the specification is corrected to what the suite verifies, or a test is added that
makes the scenario true — the design says which and why:

- **Five kills, not twenty, for the intent path** (spec corrected). The scenario says "verified with
  five kills per path — recorded and heavy — on the PostgreSQL store". The projections' twenty and the
  timers' ten stay.
- **Two activations of one aggregate** (test added). Two single-silo clusters with distinct cluster
  ids share one store — the shape a directory lapse produces — and each runs a command for the same
  aggregate; one append succeeds and the other observes a concurrency conflict. The scenario's
  verification clause names the setup.
- **A non-yielding handler on the aggregate path** (test added). The grace scenario is verified for a
  handler that awaits and for one that computes without yielding, on the aggregate path; the heavy
  path's non-yielding scenario stays as it is.
- **A failing entry inside a group that shares a position** (test added, scenario added). Unit tests on
  `CommittedBatch.ResumePositionBefore` and on the loop show the checkpoint resuming below the group and
  the group's applied entries applied again on the next read. The requirement gains the scenario and
  says that a projection tolerates that re-application as it tolerates any second delivery.
- **The three cuts of a batch** (test added, scenario added). A reader test names each cut; the
  `event-sourcing-store` reader requirement gains *One transaction holds more entries than a batch*,
  which states that such a batch is returned whole and therefore larger than the batch size.
- **The twenty-kill test asserts single application** (test extended, scenario sharpened). The test
  reads the unguarded total as well as the view and asserts each fact was applied once; the scenario
  says "none lost and, the kill falling before any read, none applied twice".
- **Consumer-visible effects:** none in code. Specification text changes in two capabilities; tests
  added or extended. Versioning: none — a test-only and specification-only change does not bump.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`:
  - *One aggregate has one writer across the cluster* — the unstable-cluster scenario names how it is
    verified.
  - *An accepted command is recorded before the call returns and resumed after a crash* — the
    acceptance-kill scenario says five kills per path; the grace scenario says it is verified for an
    awaiting and for a non-yielding handler.
  - *Projections and sagas read the store in commit order and never miss a committed fact* — the
    commit-kill scenario says none applied twice.
  - *A failing entry stops its partition, is retried, and is visible* — a failing entry inside a group
    sharing one position; new scenario.
- `event-sourcing-store`:
  - *The store can be read in commit order without skipping a late committer* — one transaction
    larger than a batch is returned whole; new scenario.

## Impact

- `openspec/specs/orleans-execution/spec.md`, `openspec/specs/event-sourcing-store/spec.md` (through
  the deltas).
- Tests: `tests/Stratara.Orleans.IntegrationTests/Aggregates/` (two clusters, one store; a non-yielding
  aggregate handler past the grace), `tests/Stratara.Orleans.IntegrationTests/CommitOrder/` (the three
  cuts), `tests/Stratara.Orleans.IntegrationTests/Projections/CommitPublishKillTests.cs` (single
  application), `tests/Stratara.Orleans.Tests/` (`CommittedBatch.ResumePositionBefore`, the loop under a
  shared position), `tests/Stratara.Orleans.Scenarios/` (a `count` command reading the unguarded total,
  a non-yielding variant of the intent scenario's handler).
- No source under `src/` changes. Versioning: none.
