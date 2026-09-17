## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.
      *Done:* approved (owner, 2026-09-17).

## 1. Five kills per path (D1)

- [x] 1.1 The scenario *The host dies after acceptance* says five kills per path; `DurableIntentTests.Kills`
      stays 5 and the class summary names the count. Verify: the delta in
      `specs/orleans-execution/spec.md`; `tests/Stratara.Orleans.IntegrationTests/Aggregates/DurableIntentTests.cs:17`.
      *Done:* the class summary names five kills per path. The delta of this change had been written before #113–#118 and would have dropped their scenarios; it was rebased by a three-way merge of each requirement against the spec it was written on.

## 2. Two activations of one aggregate (D2)

- [x] 2.1 `DuplicateActivationTests` in `tests/Stratara.Orleans.IntegrationTests/Aggregates`: two in-process
      hosts with distinct cluster ids on one store, a gated handler, one command per host for the same
      aggregate, both released; one completes and the other observes `ConcurrencyException` (scenario
      *The cluster is unstable and activates an aggregate twice*). Verify: the test green; run it once
      with the gate released one host at a time and record that no conflict occurs then.
      *Done:* the gate counts both handlers at it before the release; the conflict is found in the exception chain the forwarding caller observes. The second test in the class releases the gate first and dispatches one host after the other: both appends are stored (version 3), no conflict — kept in the suite rather than run once, since it shows the conflict is the store's version check and not the gate. Both run in about 14 s.

## 3. A non-yielding handler on the aggregate path (D3)

- [x] 3.1 The intent scenario's host gains `enqueue-blocking`, whose handler sleeps the thread for the given
      milliseconds; `A_handler_that_runs_longer_than_the_grace_runs_once` gains the row (scenario *A
      handler runs longer than the grace*). Verify: the scenario in `tests/Stratara.Orleans.Scenarios`;
      the theory's third row green with `applications = 1`.
      *Done:* the handler calls `Thread.Sleep`; the theory's third row uses its own database and ports (11336/30226).

## 4. A failing entry inside a shared-position group (D4)

- [x] 4.1 `CommittedBatchTests` in `tests/Stratara.Orleans.Tests` on `ResumePositionBefore`: index 0; distinct
      positions; a group sharing the failing entry's position with the resume below the group; a group
      that opens the batch with the resume at the position before it. Verify: four tests green.
- [x] 4.2 `StoreReaderLoopTests` gains a scripted reader whose batch holds two entries at one position and
      an applier that fails on the second: the checkpoint is written below the group and the next read
      returns both (scenario *A projection throws on the second entry of one transaction*). Verify: the
      test green; against a loop that wrote `batch.Position` it fails.
      *Done:* with the loop mutated to resume at the last applied entry's position instead of `ResumePositionBefore`, the new test fails and the other five pass; restored.

## 5. The three cuts of a batch (D5)

- [x] 5.1 `WholeTransactionReadTests` in `tests/Stratara.Orleans.IntegrationTests/CommitOrder` on the native
      reader: transactions of 1, 5, 1 entries read with a batch of 3 (held back whole; returned whole and
      larger than the batch; the rest), transactions of 2, 2, 2 with a batch of 3 (cut before the
      straddling one), `HasMore` asserted on each batch (scenario *One transaction holds more entries than
      a batch*). Verify: the test green against PostgreSQL.
      *Done:* two tests on one dedicated database, reading from the partition's head so earlier runs do not disturb them: 1, 5, 1 entries with a batch of 3 give batches of 1 (`HasMore`), 5 (`HasMore`, larger than the batch) and 1; 2, 2, 2 give batches of 2, 2 and 2 with `HasMore` on all but the last. About 10 s.

## 6. Single application under twenty kills (D6)

- [x] 6.1 `ProjectionScenario` answers `count` with the unguarded total's `created`; `CommitPublishKillTests`
      resets the total at the start of a run and asserts it equals the number of streams on the
      checkpoint path (scenario *The host dies between the commit and the wake-up*). Verify:
      `tests/Stratara.Orleans.Scenarios/Hosting/Scenarios/ProjectionScenario.cs`; the assertion in
      `CommitPublishKillTests.The_checkpoint_path_loses_no_event_where_the_bus_path_loses_every_bundle_in_flight`
      green with the count recorded in the evidence file.
      *Done:* `reset-count` clears the total when the first host of the checkpoint run starts; after the twentieth restart `count` is read until two reads two seconds apart agree, and must equal the kills. The evidence file records `applications`. The scenario host also answers `reset-count`.

## 7. Close

- [x] 7.1 `./scripts/local-gauntlet.sh` green; the `Aggregates`, `CommitOrder` and `Projections` integration
      namespaces green against PostgreSQL, with the run times of the two new integration classes recorded
      here. Verify: the run output.
      *Done:* gauntlet green. `Aggregates`, `CommitOrder` and `Projections` integration namespaces against PostgreSQL, Redis and RabbitMQ: 69 of 69 in 15 min 46 s, the twenty-kill test's single application and the non-yielding grace row included. `DuplicateActivationTests` about 14 s and `WholeTransactionReadTests` about 10 s in their own runs.

- [x] 7.2 `openspec validate align-the-execution-model-specs-with-their-tests --strict` passes. Verify: the
      output.
      *Done:* valid.

