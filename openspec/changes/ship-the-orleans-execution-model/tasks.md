## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.
- [x] 0.2 Before approval, the owner decides whether any decision may follow in a patch. A decision
      that does takes its requirements and scenarios out of this change into a change of its own, so
      nothing is archived as a guarantee the packages do not keep. Verify: the decision recorded here
      with its date, and `openspec validate --strict` green after any split.
      Decided 2026-09-14 by the owner: no decision follows in a patch; the change ships whole, in the
      minor release after 4.0.5.

## 1. Packages (D1, D12, D24)

- [ ] 1.1 `src/Stratara.Orleans/` becomes packable: `IsPackable=true`, the package metadata the other
      packages carry (id, description, tags, readme, icon), every public member documented, listed in
      `Stratara.Publish.slnf`. Verify: `dotnet pack src/Stratara.Orleans -c Release` produces a package
      and `PublishFilterCoverageTests` passes.
- [ ] 1.2 `src/Stratara.Orleans.EntityFrameworkCore/` created from today's `CommitOrder/` readers, the
      checkpoint store with `AddStrataraProjectionCheckpoints<TReadContext>()`, and the model extension
      with its provider switch; the runtime package's registrations lose their `DbContext` type
      parameter. Verify: the pack succeeds, and a test in `tests/Stratara.Orleans.Tests` asserts that
      `Stratara.Orleans` references no Entity Framework assembly and not `Microsoft.Orleans.Server`.
- [ ] 1.3 The ports move: timers, singleton work, readers, checkpoints and the rebuilder to
      `Stratara.Abstractions`; the rebuildable projection to `Stratara.Projections`; the process to
      `Stratara.Sagas`. Verify: `tests/Stratara.Orleans.Tests/SurfaceTests.cs` asserts no public type in
      the runtime package exposes `IRemindable`, and a new test asserts the projection and saga packages
      reference no Orleans assembly.
- [ ] 1.4 `VersionOverride="[10.3.1, 11.0.0)"` on the Orleans references of the two package projects;
      the central pin in `Directory.Packages.props` stays 10.3.1. Verify: the produced nuspec declares
      the range, and a consumer project that restores the two packages from a local feed built by
      `dotnet pack` compiles.
- [ ] 1.5 The published surface is trimmed (D24): the two non-promise readers move to
      `tests/Stratara.Orleans.Benchmarks`; the send lane is internal and releases completed tails; the
      native reader takes table and column names from the model; section names are bound or no longer
      claimed; a timer purpose over the column length is an `ArgumentException`; the process base class
      documents duplicate facts and the emit rule; the drain skips event bundles where none are stored;
      the block shared by the saga and projection grains is written once. Verify: `SurfaceTests` lists
      every public type of both packages and fails on an addition; a unit test for the purpose length; a
      test that the native reader works under a non-snake-case model.

## 2. Store schema and readers (D4, D7, D10, D23)

- [ ] 2.1 The model extension joins the shipped write and read models: commit-order column, position
      column, counter table, outbox attempt count, kept state, last hand-over time, aggregate id and heavy
      flag, checkpoint table, with the readers' indexes. Verify: a new test in
      `tests/Stratara.EntityFrameworkCore.Tests` asserts the entity types, columns and indexes the
      extension adds to the write and read models; the migration page names every addition.
- [ ] 2.2 Readers declare a stable name; the portable reader's name carries the partition count; the
      interceptor reads the count from `CommitOrderOptions`; the checkpoint store keys on the name.
      Verify: `InterleavedCommitTests` green for both readers; unit tests that a renamed reader class keeps
      its checkpoints and that a changed partition count is refused with a message naming both counts.
- [ ] 2.3 `CommittedBatch.HasMore`, set by both readers, the portable one now reading one row past the
      batch; the loop stops after an uncut batch. Verify: a unit test on the loop's read count per
      catch-up, and `ProjectionGrainTests` green.
- [ ] 2.4 The portable reader's backfill and its start-up refusal (D23). Verify: an integration test on a
      store with entries written before the counter — the reader refuses to start, the backfill positions
      them, and a reader started afterwards applies every entry in commit order within its partition.

## 3. Known limitations closed (D4, D5, D6, D8, D9)

- [ ] 3.1 `LogEvents.Orleans` band `117_000` and the instruments of D5 on the framework's meter,
      including the faulted catch-up and the lag from the entry's recorded time; source-generated logging
      throughout. Verify: `tests/Stratara.Orleans.Tests` asserts every id is in the band and every
      instrument name is in the published list; `docs/reference/log-events-schema.md` names the new upper
      bound and `LogEventAllocationTests` is green; `ProjectionGrainTests` asserts a stalled partition logs
      and counts.
- [ ] 3.2 Bounded resume, kept state, the hand-over lease, the aggregate id read from the record, and the
      operator's return. Verify: `DurableIntentTests` gains a handler that always throws (kept after the
      bound, the next command still resumed), a handler that outlives the grace (runs once, heavy variant
      included), an encrypted command resumed after a kill (runs in its aggregate's order), and a returned
      kept command (resumed with its count starting over).
- [ ] 3.3 Permits as leases reconciled against membership. Verify: `HeavyBurstTests` gains a case where a
      worker silo is killed holding permits and the bound is whole after the lease.
- [ ] 3.4 Starters as silo lifecycle participants. Verify: `CoHostingTests` green in both orders with the
      hosted-service starters deleted.
- [ ] 3.5 Cancellation reaches the store in the reader loop, the checkpoint store, the durable timers and
      the saga process grain. Verify: a unit test that a cancelled token stops a catch-up between batches
      without a checkpoint write.

## 4. Registrations and composition (D2, D3, D11, D15, D16, D20)

- [ ] 4.1 `AddStrataraOrleans` takes the durable directory factory and registers it; a lifecycle
      participant fails the silo when it is absent. Verify: a test that a host without the directory fails
      at start with the message, and `SingletonWorkTests` green.
- [ ] 4.2 `IOutboxRepository.DeleteManyAsync` with a default loop and the EF override; the completion queue
      goes through the port. Verify: `tests/Stratara.EntityFrameworkCore.Tests` covers the override; a fake
      repository without the override passes `IntentCompletionQueueTests`.
- [ ] 4.3 `AddEventProjectionServices` and `AddSagaServices` in `Stratara.EventSourcing.WorkerDefaults`,
      the replay worker staying in the projection services composite; the worker composites call them; the
      execution model's registrations require them and remove nothing. Verify:
      `tests/Stratara.EventSourcing.WorkerDefaults.Tests` asserts the services composites register no bus
      worker and keep the replay worker and the worker composites are unchanged; `ProjectionGrainTests`
      and `SagaGrainTests` green.
- [ ] 4.4 A full replay on a host with store-reading projections pauses their readers, returns their
      checkpoints to the beginning after truncating, and resumes them (D11). Verify: an integration test
      that replays while store readers run and finds every read model refilled.
- [ ] 4.5 Commands on the execution model's path go through `IMediator`; the enqueue-time authorizer
      decorates the registered dispatcher slot (D15). Verify: integration tests that a command failing
      validation is not handled on the intent path and that authorization applies in both registration
      orders; a unit test in `tests/Stratara.Infrastructure.Tests` for the decoration of an arbitrary
      registered dispatcher.
- [ ] 4.6 The turn marker carries the aggregate id (D16). Verify: an integration test in which a handler
      sends a command for a second aggregate while that aggregate runs another, and the two never overlap.
- [ ] 4.7 Options validated at start; enumerable timer ports composed by prefix with the start-up check;
      singleton work placed only on silos that registered it (D20). Verify: unit tests per invalid setting
      that fail the host at start naming it; a test that timer owners registered after the model fire; a
      two-silo test in which only one silo registers a singleton work and neither reports a failure.

## 5. Timers, processes, rebuild and reset (D17, D18, D19, D21, D22)

- [ ] 5.1 The timer-owner grain is reentrant (D17). Verify: integration tests in which a fact and a timeout
      for one process collide, and in which `OnTimeoutAsync` reschedules — both complete, and the timeout
      fires once.
- [ ] 5.2 A process step registers its timers before its append; the process contract documents it (D18).
      Verify: a kill test between the registration and the append in which the timeout fires once after
      the fact is applied again; `SagaProcessTimeoutTests` asserts a timer exists before the kill.
- [ ] 5.3 The due-time tolerance option (D19). Verify: a unit test with a fake time provider in which a
      tick slightly before the due time fires.
- [ ] 5.4 The rebuilder resets checkpoints before truncating (D21). Verify: `ProjectionRebuilder` end to end
      against a real store, including a truncation that throws, after which the projection re-reads from
      the beginning.
- [ ] 5.5 `IExecutionModelReset`, its storage implementation in the persistence package and the host's
      directory callback (D22). Verify: `ResetTests` run against the port instead of the test-only reset.

## 6. Documentation and gates (D13, D26)

- [ ] 6.1 `docs/` pages: the capability; the migration page, with the portable reader's backfill and the
      full-replay note; the operations page, with the hard-death scenario and its three answers, the
      statement that returns a kept command, and the host code for a Redis-backed directory. Verify:
      `tests/Stratara.Documentation.Tests` green and every API the pages name exists.
- [ ] 6.2 `llms.txt` core facts; `README.md` and the landing page name the recommended model; `CHANGELOG.md`
      unreleased entry; the package count 25 to 27 everywhere D13 lists; the log-event range; a
      cheatsheet row per new registration; the pin comment in `Directory.Packages.props`; `llms-full.txt`
      regenerated with `dotnet run --project tools/Stratara.ReferenceCatalogue -- llms-full.txt`. Verify:
      `ReferenceCatalogueIsCurrentTests`, `DiCheatsheetCoverageTests`, `LogEventAllocationTests` and
      `PublishFilterCoverageTests` green, and no page states 25 packages.
- [ ] 6.3 The coverage exclusion for `src/Stratara.Orleans/**` leaves `.github/workflows/sonar.yml`, and the
      analysis collects the Orleans integration suite's in-process coverage (D26). Verify: the first
      analysis of `main` after the merge reports the quality gate as passed with the package in the
      coverage measure.

## 7. Tests and evidence (D14, D25)

- [ ] 7.1 The scenario host's path from build-time metadata with an environment fallback; the integration
      project builds the host; the scenario-host step leaves `.github/workflows/integration.yml`. Verify:
      the workflow green without the step, and the suite green when run from a custom output directory.
- [ ] 7.2 Kill tests with due times relative to a start signal and asserted preconditions; singleton work
      taken over by the surviving silo; a timer across two silos. Verify: the integration workflow green
      three runs in a row on the branch.
- [ ] 7.3 B3 and B5 on the packaged code, production profile, built-in directory, diagnostics on; compared
      with the archived optimised numbers in `evidence/results.md`. Verify:
      `evidence/raw/commands-per-aggregate/` and `evidence/raw/resources/` exist and the comparison is
      within 10 % or explained.
- [ ] 7.4 `tests/Stratara.Orleans.IntegrationTests` green on the packaged projects, 37 tests plus the new
      cases. Verify: the run's summary in `evidence/results.md`.
- [ ] 7.5 `./scripts/local-gauntlet.sh` green; `openspec validate --strict` green; `/bump-version minor`
      queued for after the merge. Verify: the gauntlet's last line and `git diff --stat main`.
