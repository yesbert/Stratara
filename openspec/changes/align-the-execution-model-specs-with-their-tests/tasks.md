## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. Five kills per path (D1)

- [ ] 1.1 The scenario *The host dies after acceptance* says five kills per path; `DurableIntentTests.Kills`
      stays 5 and the class summary names the count. Verify: the delta in
      `specs/orleans-execution/spec.md`; `tests/Stratara.Orleans.IntegrationTests/Aggregates/DurableIntentTests.cs:17`.

## 2. Two activations of one aggregate (D2)

- [ ] 2.1 `DuplicateActivationTests` in `tests/Stratara.Orleans.IntegrationTests/Aggregates`: two in-process
      hosts with distinct cluster ids on one store, a gated handler, one command per host for the same
      aggregate, both released; one completes and the other observes `ConcurrencyException` (scenario
      *The cluster is unstable and activates an aggregate twice*). Verify: the test green; run it once
      with the gate released one host at a time and record that no conflict occurs then.

## 3. A non-yielding handler on the aggregate path (D3)

- [ ] 3.1 The intent scenario's host gains `enqueue-blocking`, whose handler sleeps the thread for the given
      milliseconds; `A_handler_that_runs_longer_than_the_grace_runs_once` gains the row (scenario *A
      handler runs longer than the grace*). Verify: the scenario in `tests/Stratara.Orleans.Scenarios`;
      the theory's third row green with `applications = 1`.

## 4. A failing entry inside a shared-position group (D4)

- [ ] 4.1 `CommittedBatchTests` in `tests/Stratara.Orleans.Tests` on `ResumePositionBefore`: index 0; distinct
      positions; a group sharing the failing entry's position with the resume below the group; a group
      that opens the batch with the resume at the position before it. Verify: four tests green.
- [ ] 4.2 `StoreReaderLoopTests` gains a scripted reader whose batch holds two entries at one position and
      an applier that fails on the second: the checkpoint is written below the group and the next read
      returns both (scenario *A projection throws on the second entry of one transaction*). Verify: the
      test green; against a loop that wrote `batch.Position` it fails.

## 5. The three cuts of a batch (D5)

- [ ] 5.1 `WholeTransactionReadTests` in `tests/Stratara.Orleans.IntegrationTests/CommitOrder` on the native
      reader: transactions of 1, 5, 1 entries read with a batch of 3 (held back whole; returned whole and
      larger than the batch; the rest), transactions of 2, 2, 2 with a batch of 3 (cut before the
      straddling one), `HasMore` asserted on each batch (scenario *One transaction holds more entries than
      a batch*). Verify: the test green against PostgreSQL.

## 6. Single application under twenty kills (D6)

- [ ] 6.1 `ProjectionScenario` answers `count` with the unguarded total's `created`; `CommitPublishKillTests`
      resets the total at the start of a run and asserts it equals the number of streams on the
      checkpoint path (scenario *The host dies between the commit and the wake-up*). Verify:
      `tests/Stratara.Orleans.Scenarios/Hosting/Scenarios/ProjectionScenario.cs`; the assertion in
      `CommitPublishKillTests.The_checkpoint_path_loses_no_event_where_the_bus_path_loses_every_bundle_in_flight`
      green with the count recorded in the evidence file.

## 7. Close

- [ ] 7.1 `./scripts/local-gauntlet.sh` green; the `Aggregates`, `CommitOrder` and `Projections` integration
      namespaces green against PostgreSQL, with the run times of the two new integration classes recorded
      here. Verify: the run output.
- [ ] 7.2 `openspec validate align-the-execution-model-specs-with-their-tests --strict` passes. Verify: the
      output.
