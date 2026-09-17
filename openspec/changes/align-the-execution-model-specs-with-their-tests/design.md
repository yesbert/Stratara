## Context

See `proposal.md` — Why. What the suite verifies today, on `main` after #109:

- **Kills.** `DurableIntentTests.RunKillsAsync` (`tests/Stratara.Orleans.IntegrationTests/Aggregates/DurableIntentTests.cs:103-123`)
  loops `Kills = 5` times per path (`enqueue`, `enqueue-heavy`), each iteration a fresh host process,
  a five-second handler, a kill, a restart and a wait of up to thirty seconds; one iteration is
  ten to twenty seconds. `CommitPublishKillTests` (`Projections/CommitPublishKillTests.cs:19`) loops
  twenty times; `HardKillTimerTests` (`Timers/HardKillTimerTests.cs:16`) ten.
- **Two activations.** `PocSilo.Configure` (`tests/Stratara.Orleans.Scenarios/Hosting/PocSilo.cs:75-84`)
  gives every silo port its own cluster id unless a test passes one, and `CoHostingTests` and
  `RoleSplitTests` start two in-process hosts; two hosts with different cluster ids that share one
  store connection string are two clusters, each with an activation of the same aggregate key. The
  aggregate grain appends through `IEventSource`, and the store's version constraint raises
  `ConcurrencyException` (`event-sourcing-store`, *A concurrency conflict discards the batch*).
- **The grace.** `DurableIntentTests.A_handler_that_runs_longer_than_the_grace_runs_once` (`:51-66`)
  enqueues through the intent scenario with an eight-second `Task.Delay` handler and asserts
  `applications = 1` four seconds after completion. `HeavyNonYieldingTests` (`HeavyWork/HeavyNonYieldingTests.cs:19-62`)
  runs a five-second `Thread.Sleep` heavy handler under a two-second grace in-process. The aggregate
  path's lease renews from a `TimeProvider` timer (`IntentLease.cs`, #107 D3), verified by
  `IntentLeaseRenewalTests` against a blocked scheduler, not by an integration test.
- **Shared positions.** `CommittedBatch.ResumePositionBefore` (`src/Stratara.Abstractions/Abstractions/CommitOrder/ICommittedPositionReader.cs:99-115`)
  walks back from the first unapplied entry to the highest position strictly below its position, or
  returns the position before the batch. `StoreReaderLoop.ReadAndApplyAsync` (`src/Stratara.Orleans/Projections/StoreReaderLoop.cs:211-220`)
  writes that position when a batch applied partly. The scripted readers in `StoreReaderLoopTests`
  (`tests/Stratara.Orleans.Tests/StoreReaderLoopTests.cs:94-113`), `StoreReaderCancellationTests` and
  `StoreReaderFailureTests` hand out one position per entry.
- **The cuts.** `PostgresTransactionIdReader.ReadAfterAsync` (`src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PostgresTransactionIdReader.cs:78-96`)
  reads `batchSize + 1` rows; when the overflow row's transaction equals the last complete row's, it
  cuts before that transaction, and when that leaves nothing (the transaction fills the whole batch and
  more) it reads the transaction whole (`:115-124`). `TransactionIdMigrationTests` (`CommitOrder/TransactionIdMigrationTests.cs:24-26,51-72`)
  reads with a batch of 3 over 25 entries backfilled in transactions of 10 across three buckets and
  asserts order and `largestBatch <= BackfillBatch`. `InterleavedCommitTests` writes one entry per
  transaction.
- **Presence only.** `ProjectionScenario`'s `view` command (`tests/Stratara.Orleans.Scenarios/Hosting/Scenarios/ProjectionScenario.cs:136-155`)
  answers *present* or *absent* from `poc_counter_view`, whose projection guards on version
  (`Projections/CounterProbe.cs:58-70`). The same assembly registers `CounterTotalsProjection`
  (`Projections/AuditProbe.cs:35-57`), an unguarded running total, into every host that calls
  `AddProjectionsFromAssemblyContaining<ProjectionScenario>()`, and `KillingBundleDispatcher` ends the
  process at the dispatcher call — after the commit, before any nudge or read.

## Goals / Non-Goals

**Goals:**
- Every "verified …" clause in the two capabilities is true of a test in the suite, by name.
- Every guarantee the two requirements state about a partial batch and a whole transaction has a test
  that fails when it is broken.

**Non-Goals:**
- Raising the intent kill count to twenty. The acceptance-kill case is deterministic — the record is
  written, the host dies before a five-second handler ends, the restart resumes — and its evidence does
  not grow with repetitions the way the commit-publish race's does; twenty would add five minutes to
  a suite that runs twenty minutes for nothing the fifth kill did not show.
- Producing a genuine directory lapse inside one cluster. Two clusters on one store exercise the same
  code — two activations, two appends, one constraint — without a harness for split membership.
- Testing the portable reader's cuts. It orders by one position per entry, has no transaction grouping
  and is verified on PostgreSQL only; the whole-transaction read is the native reader's.
- Any change under `src/`.

## Decisions

### D1 — Five kills per path: the specification is corrected

*The host dies after acceptance* says "verified with five kills per path, recorded and heavy, on the
PostgreSQL store". The test stays at `Kills = 5`.

*Rejected: `Kills = 20`.* See Non-Goals; the projections' twenty stays because that test measures a
race the bus path loses in every iteration.

Evidence: `DurableIntentTests.cs:17,24-31,103-123`.

### D2 — Two activations of one aggregate: a test is added

`DuplicateActivationTests` in `Aggregates`: two in-process hosts through `PocHosting`, each with its own
cluster id and ports, both with `AddStrataraAggregateGrains` against one store; a gate handler that
appends once released; both hosts dispatch a command for the same aggregate id through the mediator;
the gate is released for both; one dispatch completes and the other observes `ConcurrencyException`
(or its resumption on the recorded path — the test uses the forwarding path so the caller sees it).
The scenario's verification clause names the setup so a reader knows it is a simulation of a lapse,
not a lapse.

*Rejected: a `ClusterMembershipOptions` profile that partitions one cluster.* Orleans has no setting
that produces a split; a test that kills the directory's Redis mid-call would test Redis.

Evidence: `PocSilo.cs:75-84`; `CoHostingTests`, `RoleSplitTests` (two hosts in one test);
`HeavyConflictTests` (a concurrency conflict observed through the model).

### D3 — A non-yielding handler on the aggregate path: a test is added

The intent scenario gains an `enqueue-blocking` variant whose handler sleeps the thread for the given
milliseconds; `A_handler_that_runs_longer_than_the_grace_runs_once` gains a third theory row with it
under the same eight-second delay. The scenario's clause says the grace is verified for an awaiting and
for a non-yielding handler on the aggregate path and for an awaiting one on the heavy path, which with
the heavy non-yielding scenario beside it is the full matrix.

*Rejected: leaving the heavy test as the only non-yielding evidence.* The lease the aggregate path holds
is a different object from the permit the heavy path renews; both were fixed in #107 and only one is
verified end to end.

Evidence: `DurableIntentTests.cs:51-66`; `HeavyNonYieldingTests.cs:34-62`; the intent scenario's
handler in `tests/Stratara.Orleans.Scenarios`.

### D4 — A failing entry inside a shared-position group: tests and a scenario are added

Unit tests on `CommittedBatch.ResumePositionBefore` in `tests/Stratara.Orleans.Tests`: index 0 returns
the position before the batch; a failing entry after distinct positions returns the last applied
position; a failing entry whose position the applied entries before it share returns the highest
position below the group, or the position before the batch when the group opens it. A loop test with a
scripted reader whose batch holds two entries at one position and a batch applier that fails on the
second: the checkpoint is written below the group, and the next read returns both entries again. The
requirement gains the sentence and the scenario, which is what a consumer observes: the first entry of
a transaction is applied twice when the second fails.

Evidence: `ICommittedPositionReader.cs:67-77,99-115`; `StoreReaderLoop.cs:211-220`;
`StoreReaderLoopTests.cs:94-113` (the scripted reader to extend with shared positions).

### D5 — The three cuts: a reader test and a scenario are added

`WholeTransactionReadTests` in `CommitOrder` on the native reader, with `PocStore` appending under
explicit transactions into one partition: transactions of 1, 5, 1 entries read with a batch of 3 — the
first batch holds the single entry (the five-entry transaction is held back whole), the second holds
the five (larger than the batch), the third the last one; and transactions of 2, 2, 2 read with a batch
of 3 — the first batch holds two (the second transaction would straddle), and so on. `HasMore` is
asserted on each. The `event-sourcing-store` requirement states the two rules the cut implements and
gains the scenario.

*Rejected: asserting the cuts inside `TransactionIdMigrationTests`.* That test verifies the backfill's
grouping; the cuts are the reader's and deserve a test whose failure names them.

Evidence: `PostgresTransactionIdReader.cs:78-124`; `TransactionIdMigrationTests.cs:24-26,51-72`;
`InterleavedCommitTests` (the `PocStore` fixture and explicit transactions to reuse).

### D6 — The twenty-kill test asserts single application: the test is extended and the scenario sharpened

`ProjectionScenario` gains a `count` command answering `poc_counter_totals.created`; the test resets the
totals row at the start of a run and asserts, after the twenty kills, that the checkpoint path's total
equals the number of streams appended, beside `Assert.Empty(grainLost)`. The scenario says "none lost
and, the kill falling before any read, none applied twice" — the qualification matters, because a kill
between an application and its checkpoint write legitimately applies again, and that is a different
scenario (*The host dies after the handler completed* on the command path; at-least-once on the
projections capability).

*Rejected: asserting single application on the bus and durable-bundle paths as well.* The bus path
loses bundles in this test by design; the durable-bundle path delivers through the outbox worker, and a
duplicate there is at-least-once behaviour the bus capability already states.

Evidence: `CommitPublishKillTests.cs:52-84`; `ProjectionScenario.cs:118-160`; `AuditProbe.cs:35-57`;
`KillingBundleDispatcher` (the kill point).

## Risks / Trade-offs

- [The two-cluster test leaves two membership tables' worth of rows in `poc_orleans`] → each host
  gets its own cluster id, as every kill test does; the reset scopes by cluster id.
- [A non-yielding handler in the PoC host blocks a silo thread for eight seconds] → the heavy test does
  the same for five; the test profile's reminder period is one second and the lease is renewed from a
  timer off that thread, which is what the test shows.
- [The whole-transaction test depends on PostgreSQL assigning one transaction id per `BeginTransaction`]
  → that is the native reader's premise and what `InterleavedCommitTests` already relies on.
- [The totals row accumulates across reruns against the same database] → the test resets it at the
  start of each run.

## Migration Plan

None. Specification text and tests only; no package changes, no version bump.
