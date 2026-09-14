## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. Packages (D1, D12)

- [ ] 1.1 `src/Stratara.Orleans/` becomes packable: `IsPackable=true`, package metadata per
      `.claude/docs/packable-projects-checklist.md`, every public member documented, listed in
      `Stratara.Publish.slnf`. Verify: `dotnet pack src/Stratara.Orleans -c Release` produces a package
      and `PublishFilterCoverageTests` passes.
- [ ] 1.2 `src/Stratara.Orleans.EntityFrameworkCore/` created from today's `CommitOrder/` readers,
      the checkpoint store and the model extension; packable, in the filter, documented. Verify: the
      pack succeeds and `tests/Stratara.Orleans.IntegrationTests` compiles against it.
- [ ] 1.3 The ports move: timers, singleton work, readers, checkpoints and the rebuilder to
      `Stratara.Abstractions`; the rebuildable projection to `Stratara.Projections`; the process to
      `Stratara.Sagas`. Verify: `tests/Stratara.Orleans.Tests/SurfaceTests.cs` asserts no public type
      in the runtime package exposes `IRemindable`, and a new test asserts the projection and saga
      packages reference no Orleans assembly.
- [ ] 1.4 Orleans dependency range `[10.3.1, 11.0.0)` in the two csproj files, the pin unchanged in
      `Directory.Packages.props`. Verify: the produced nuspec declares the range.

## 2. Store schema and readers (D7, D10)

- [ ] 2.1 The model extension joins the shipped write and read models: commit-order column, position
      column, counter table, outbox attempt count and kept state, checkpoint table, with the readers'
      indexes. Verify: `tests/Stratara.EntityFrameworkCore.Tests` model snapshot test updated and green;
      the migration note names every addition.
- [ ] 2.2 Readers declare a stable name; the checkpoint store keys on it. Verify:
      `InterleavedCommitTests` green for both readers and a unit test that a renamed reader class keeps
      its checkpoints.
- [ ] 2.3 `CommittedBatch.HasMore`, set by both readers; the loop stops after an uncut batch. Verify: a
      unit test on the loop's read count per catch-up, and `ProjectionGrainTests` green.

## 3. Known limitations closed (D4, D5, D6, D8, D9)

- [ ] 3.1 `LogEvents.Orleans` band `117_000` and the instruments of D5 on the framework's meter;
      source-generated logging throughout the package. Verify: `tests/Stratara.Orleans.Tests` asserts
      every id is in the band and every instrument name is in the published list;
      `ProjectionGrainTests` asserts a stalled partition logs and counts.
- [ ] 3.2 Bounded resume and kept state on the outbox record; the drain continues past a failing
      hand-over. Verify: `DurableIntentTests` gains a case where a handler always throws — kept after
      the bound, the next command still resumed.
- [ ] 3.3 Permits as leases reconciled against membership. Verify: `HeavyBurstTests` gains a case
      where a worker silo is killed holding permits and the bound is whole after the lease.
- [ ] 3.4 Starters as silo lifecycle participants. Verify: `CoHostingTests` green in both orders with
      the hosted-service starters deleted.
- [ ] 3.5 Cancellation reaches the store in the reader loop, the checkpoint store, the durable timers
      and the saga process grain. Verify: a unit test that a cancelled token stops a catch-up between
      batches without a checkpoint write.

## 4. Registrations (D2, D3, D11)

- [ ] 4.1 `AddStrataraOrleans` takes the durable directory factory and registers it; a lifecycle
      participant fails the silo when it is absent. Verify: a test that a host without the directory
      fails at start with the message, and `SingletonWorkTests` green.
- [ ] 4.2 `IOutboxRepository.DeleteManyAsync` with a default loop and the EF override; the completion
      queue goes through the port. Verify: `tests/Stratara.EntityFrameworkCore.Tests` covers the
      override; a fake repository without the override passes `IntentCompletionQueueTests`.
- [ ] 4.3 `AddEventProjectionServices` and `AddSagaServices`; the worker composites call them; the
      execution model's registrations require them and remove nothing. Verify: the composites' tests in
      `tests/Stratara.Infrastructure.Tests` and `ProjectionGrainTests`, `SagaGrainTests` green.

## 5. Documentation (D13)

- [ ] 5.1 `docs/` pages: the capability, the migration note, the operations note with the hard-death
      scenario and its three answers. Verify: `tests/Stratara.Documentation.Tests` green and every API
      the pages name exists.
- [ ] 5.2 `llms.txt` and `llms-full.txt` core facts; `README.md` and the landing page name the
      recommended model; `CHANGELOG.md` unreleased entry. Verify: the documentation tests and a read of
      the three files.

## 6. Evidence and close (D14)

- [ ] 6.1 The owner decides whether any tail item (D6–D10) may follow in a patch. Verify: the decision
      recorded here with the date.
- [ ] 6.2 B3 and B5 on the packaged code, production profile, built-in directory, diagnostics on;
      compared with the archived optimised numbers in `evidence/results.md`. Verify:
      `evidence/raw/commands-per-aggregate/` and `evidence/raw/resources/` exist and the comparison is
      within 10 % or explained.
- [ ] 6.3 `tests/Stratara.Orleans.IntegrationTests` green against the packages, 37 tests plus the new
      cases. Verify: the run's summary in `evidence/results.md`.
- [ ] 6.4 `./scripts/local-gauntlet.sh` green; `openspec validate --strict` green; `/bump-version minor`
      queued for after the merge. Verify: the gauntlet's last line and `git diff --stat main`.
