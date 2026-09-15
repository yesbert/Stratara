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

- [x] 1.1 `src/Stratara.Orleans/` becomes packable: `IsPackable=true`, the package metadata the other
      packages carry (id, description, tags, readme, icon), every public member documented, listed in
      `Stratara.Publish.slnf`. Verify: `dotnet pack src/Stratara.Orleans -c Release` produces a package
      and `PublishFilterCoverageTests` passes.
- [x] 1.2 `src/Stratara.Orleans.EntityFrameworkCore/` created from today's `CommitOrder/` readers, the
      checkpoint store with `AddStrataraProjectionCheckpoints<TReadContext>()` and the partition-counter
      interceptor; the runtime package's registrations lose their `DbContext` type parameter. Verify: the
      pack succeeds, and a test in `tests/Stratara.Orleans.Tests` asserts that `Stratara.Orleans`
      references no Entity Framework assembly and not `Microsoft.Orleans.Server`.
      Done: `PackageBoundaryTests` walks the runtime package's own dependency graph. The schema additions
      are declared by the shipped store package instead (2.1; owner decision 2026-09-15).
- [x] 1.3 The ports move: timers, singleton work, readers, checkpoints and the rebuilder to
      `Stratara.Abstractions`; the rebuildable projection to `Stratara.Projections`; the process to
      `Stratara.Sagas`. Verify: `tests/Stratara.Orleans.Tests/SurfaceTests.cs` asserts no public type in
      the runtime package exposes `IRemindable`, and a new test asserts the projection and saga packages
      reference no Orleans assembly.
- [x] 1.4 `VersionOverride="[10.3.1, 11.0.0)"` on the Orleans references of the two package projects;
      the central pin in `Directory.Packages.props` stays 10.3.1. Verify: the produced nuspec declares
      the range, and a consumer project that restores the two packages from a local feed built by
      `dotnet pack` compiles.
      Done: only the runtime package references Orleans directly; the persistence package takes it
      through `Stratara.Orleans`. Nuspec shows `[10.3.1, 11.0.0)` for Reminders, Runtime and Sdk; a
      consumer on the local feed with `Microsoft.Orleans.Server` 10.3.1 compiles.
- [x] 1.5 The published surface is trimmed (D24): the two non-promise readers move to
      `tests/Stratara.Orleans.Benchmarks`; the send lane is internal and releases completed tails; the
      native reader takes table and column names from the model; section names are bound or no longer
      claimed; a timer purpose over the column length is an `ArgumentException`; the process base class
      documents duplicate facts and the emit rule; the drain skips event bundles where none are stored;
      the block shared by the saga and projection grains is written once. Verify: `SurfaceTests` lists
      every public type of both packages and fails on an addition; a unit test for the purpose length; a
      test that the native reader works under a non-snake-case model.
      Done. The two non-promise readers live in `tests/Stratara.Orleans.IntegrationTests/CommitOrder/`,
      which the benchmarks reference: `InterleavedCommitTests` needs the naive reader to prove it still
      provokes the interleaving. Section names are no longer claimed. `SurfaceTrimTests` covers the purpose
      length and the drain; `NativeReaderModelNamesTests` the non-snake-case model.

## 2. Store schema and readers (D4, D7, D10, D23)

- [ ] 2.1 The shipped write and read contexts in `Stratara.EventSourcing.EntityFrameworkCore` declare the
      additions, with the provider switch for the commit-order column, and the counter and checkpoint
      entities move there (owner decision 2026-09-15): commit-order column, position
      column, counter table, outbox attempt count, kept state, last hand-over time, aggregate id and heavy
      flag, checkpoint table, with the readers' indexes. Verify: a new test in
      `tests/Stratara.EntityFrameworkCore.Tests` asserts the entity types, columns and indexes the
      extension adds to the write and read models; the migration page names every addition.
      Code part done: `StoreSchemaAdditionsTests` in `tests/Stratara.EntityFrameworkCore.Tests`. Stays open
      until the migration page of 6.1 names every addition.
- [x] 2.2 Readers declare a stable name; the portable reader's name carries the partition count; the
      interceptor reads the count from `CommitOrderOptions`; the checkpoint store keys on the name.
      Verify: `InterleavedCommitTests` green for both readers; unit tests that a renamed reader class keeps
      its checkpoints and that a changed partition count is refused with a message naming both counts.
      Done: both shipped readers carry the partition count in their name (`postgres-transaction-id/16`,
      `partition-counter/16`) — the spec refuses a position under another count for either reader, not only
      the portable one. `InterleavedCommitTests` green for both readers; `StoreReaderLoopTests` (renamed class,
      changed count) and `ProjectionCheckpointStoreTests` (both counts named) in `tests/Stratara.Orleans.Tests`.
- [x] 2.3 `CommittedBatch.HasMore`, set by both readers, the portable one now reading one row past the
      batch; the loop stops after an uncut batch. Verify: a unit test on the loop's read count per
      catch-up, and `ProjectionGrainTests` green.
      Done: `StoreReaderLoopTests` counts one read for a catch-up shorter than a batch and three for 25 entries
      at a batch of 10; `ProjectionGrainTests` 3/3 and `SagaGrainTests` green.
- [x] 2.4 The portable reader's backfill and its start-up refusal (D23). Verify: an integration test on a
      store with entries written before the counter — the reader refuses to start, the backfill positions
      them, and a reader started afterwards applies every entry in commit order within its partition.
      Done: `PartitionCounterBackfill.RunAsync` positions unpositioned entries per partition ahead of those
      appended since, under the partition's counter row; `AddStrataraPortableCounterReader<TWriteContext>()`
      registers the reader with a start-up check that names the backfill. `PartitionCounterBackfillTests` green.

## 3. Known limitations closed (D4, D5, D6, D8, D9)

- [x] 3.1 `LogEvents.Orleans` band `117_000` and the instruments of D5 on the framework's meter,
      including the faulted catch-up and the lag from the entry's recorded time; source-generated logging
      throughout. Verify: `tests/Stratara.Orleans.Tests` asserts every id is in the band and every
      instrument name is in the published list; `docs/reference/log-events-schema.md` names the new upper
      bound and `LogEventAllocationTests` is green; `ProjectionGrainTests` asserts a stalled partition logs
      and counts.
      Done: the counters and up-down counters are fields with name constants on `ApplicationDiagnostics.Metrics`
      (added to `ObservabilityMetricsTests`); the lag gauge is created by the runtime package on the same meter
      under the published `OrleansReaderLagName`. Ids for kept commands, released permits and the directory check
      are allocated here and emitted by 3.2, 3.3 and 4.1. `DiagnosticsTests` in `tests/Stratara.Orleans.Tests`.
- [x] 3.2 Bounded resume behind `ICommandIntentStore` (Abstractions) with its implementation and
      `AddStrataraIntentStore<TWriteContext>()` in the persistence package (owner decision 2026-09-15), kept
      state, the hand-over lease, the aggregate id read from the record, and the operator's return. Verify:
      `DurableIntentTests` gains a handler that always throws (kept after the bound, the next command still
      resumed), a handler that outlives the grace (runs once, heavy variant included), an encrypted command
      resumed after a kill (runs in its aggregate's order), and a returned kept command (resumed with its
      count starting over); a host with the dispatcher and without the intent store fails at start naming it.
      Done: `DurableIntentTests` (bound and kept, long handler plain and heavy, order by the recorded aggregate for a
      command that names it only through the interface and seals a note, operator's return) and
      `IntentStoreStartupCheckTests`; integration suite 44/44.
- [x] 3.3 Permits as leases reconciled against membership. Verify: `HeavyBurstTests` gains a case where a
      worker silo is killed holding permits and the bound is whole after the lease.
      Done: the permit grain records holder silo and expiry per unit and releases a permit whose lease lapsed or whose
      holder the cluster declared dead, on every acquisition and on its own timer; `HeavyWorkOptions.PermitLease`,
      renewal at half the lease. `HeavyBurstTests` kills a worker process holding every permit while the permit grain
      lives on the surviving silo; the bound is whole again after the lease. 2/2.
- [x] 3.4 Starters as silo lifecycle participants. Verify: `CoHostingTests` green in both orders with the
      hosted-service starters deleted.
      Done: the singleton-work and store-reader starters subscribe to `ServiceLifecycleStage.Active`; the hosted-service
      starters are gone. `CoHostingTests` now registers singleton work with the durable directory and asserts it runs in
      both registration orders; `SingletonWorkTests`, `ProjectionGrainTests` and `SagaGrainTests` green (7/7).
- [x] 3.5 Cancellation reaches the store in the reader loop, the checkpoint store, the durable timers and
      the saga process grain. Verify: a unit test that a cancelled token stops a catch-up between batches
      without a checkpoint write.
      Done: the reader loop passes its token to the reader, the checkpoint store, the preceding-fact pipeline and the
      apply step and stops at the next batch boundary; the grain timer passes its token; the timer-owner grain and
      `DurableTimers` carry the caller's token; the saga process grain passes its token to every store call.
      `StoreReaderCancellationTests`; integration suite 45/45.

## 4. Registrations and composition (D2, D3, D11, D15, D16, D20)

- [x] 4.1 `AddStrataraOrleans` takes the durable directory factory and registers it; a lifecycle
      participant fails the silo when it is absent. Verify: a test that a host without the directory fails
      at start with the message, and `SingletonWorkTests` green.
      Done, with one shape change: the Redis grain directory has no public type or constructor, only
      `AddRedisGrainDirectory(name, ...)`, so `AddStrataraOrleans` takes a delegate that registers the directory on the
      silo builder under the name it is given (a plain factory fits through `AddGrainDirectory(name, factory)`). A
      lifecycle participant at `RuntimeInitialize`, registered by every registration whose grains need the directory,
      logs `117_107` and fails the silo naming `AddStrataraOrleans`. `DurableDirectoryCheckTests` (without and with),
      `SingletonWorkTests` and `CoHostingTests` green (5/5); the proof-of-concept silos use the registration.
- [x] 4.2 `IOutboxRepository.DeleteManyAsync` with a default loop and the EF override; the completion queue
      goes through the port. Verify: `tests/Stratara.EntityFrameworkCore.Tests` covers the override; a fake
      repository without the override passes `IntentCompletionQueueTests`.
      Done ahead of group 4, because the runtime package lost its Entity Framework reference in 1.2. The
      override is covered by `OutboxRepositoryDeleteManyTests` in `tests/Stratara.WriteStore.Tests`, beside
      the other repository tests and the SQLite setup they share (the in-memory provider of
      `Stratara.EntityFrameworkCore.Tests` cannot execute a bulk delete); the fake repository is
      `IntentCompletionThroughThePortTests`.
- [x] 4.3 `AddEventProjectionServices` and `AddSagaServices` in `Stratara.EventSourcing.WorkerDefaults`,
      the replay worker staying in the projection services composite; the worker composites call them; the
      execution model's registrations require them and remove nothing. Verify:
      `tests/Stratara.EventSourcing.WorkerDefaults.Tests` asserts the services composites register no bus
      worker and keep the replay worker and the worker composites are unchanged; `ProjectionGrainTests`
      and `SagaGrainTests` green.
      Done: the runtime without the bus-fed worker is registered by `AddProjectionHandling` and `AddSagaHandling` in the
      projection and saga packages (idempotent); the worker registrations and composites build on them. The Orleans
      registrations remove nothing; the grain hosts use the services composites, the hybrid scenario the worker composite.
      `WorkerDefaultsCompositesTests` (14), `ProjectionGrainTests` and `SagaGrainTests` (4/4); cheatsheet rows added and
      `llms-full.txt` regenerated.
- [x] 4.4 A full replay on a host with store-reading projections pauses their readers, returns their
      checkpoints to the beginning after truncating, and resumes them (D11). Verify: an integration test
      that replays while store readers run and finds every read model refilled.
      Done: `AddStrataraProjectionGrains` wraps the host's `IProjectionViewTruncator`: the projection grains are paused,
      their checkpoints returned to the beginning, the read models emptied, the grains resumed however the truncation
      ended (the reset comes before the truncation, as D21 orders it for a rebuild). A truncator registered after the
      store readers fails the host at start. `ReplayCheckpointResetTests` and `ReplayWithStoreReadersTests` (checkpoints
      at 0 when the models are emptied, every read model refilled, readers advanced again) green.
- [x] 4.5 Commands on the execution model's path go through `IMediator`; the enqueue-time authorizer
      decorates the registered dispatcher slot (D15). Verify: integration tests that a command failing
      validation is not handled on the intent path and that authorization applies in both registration
      orders; a unit test in `tests/Stratara.Infrastructure.Tests` for the decoration of an arbitrary
      registered dispatcher.
      Done: a recorded intent (aggregate, runner and heavy grains) is dispatched through `IMediator` with a cached
      generic invoker; the synchronous forward keeps invoking the handler directly, because its pipeline already ran
      on the caller's side and would otherwise validate and audit twice. `AddAuthorizingCommandOutboxDispatcher`
      moves the last registered dispatcher to a keyed slot (key `typeof(ICommandOutboxDispatcher)`) and decorates it;
      `AddStrataraOrleansCommandDispatcher` replaces that keyed slot when it exists, the plain one otherwise;
      `OutboxDrainWork` resolves the Orleans dispatcher by its own type. Deviation: authorization in both orders is
      verified against the real registrations in `EnqueueAuthorizationCompositionTests` (Orleans unit tests), not on
      a silo — a `[RequireRole]` command in the integration assembly fails the start of every host there that
      registers the plain mediator (`AuthorizationStartupValidator` scans the AppDomain). `IntentPipelineTests`
      (integration), `AuthorizingCommandOutboxDispatcherDecorationTests` (4) and `DurableIntentTests`/`ArrivalOrderTests`
      as regression (10/10) green.
- [x] 4.6 The turn marker carries the aggregate id (D16). Verify: an integration test in which a handler
      sends a command for a second aggregate while that aggregate runs another, and the two never overlap.
      Done: the ambient turn holds the id of the command's aggregate (restoring the previous one on exit); the
      forwarding behaviour lets through only a command for that aggregate. Heavy work marks its own command's
      aggregate, so it still runs outside the aggregate's activation. `CrossAggregateSendTests` green, with
      `ArrivalOrderTests`, `IntentPipelineTests`, `DurableIntentTests` and `HeavyBurstTests` as regression (13/13).
- [x] 4.7 Options validated at start; enumerable timer ports composed by prefix with the start-up check;
      singleton work placed only on silos that registered it (D20). Verify: unit tests per invalid setting
      that fail the host at start naming it; a test that timer owners registered after the model fire; a
      two-silo test in which only one silo registers a singleton work and neither reports a failure.
      Done: `OrleansOptionsValidator` validates all eight settings types on start (positive sizes and limits, polls
      longer than zero, reminder periods at or above the runtime's minimum, grace longer than the completion window,
      tolerance shorter than the retry period); `OrleansOptionsValidatorTests` (18 invalid settings + defaults).
      Timer ports are resolved as enumerables: a prefixed port (processes, `saga:`) serves its owners, the host's one
      unprefixed port every other owner; `TimerPortsStartupCheck` refuses two host owner checks, one without a
      handler, and processes without their timers; the captured `HostTimerServices` is gone. `TimerPortsTests` shows
      host ports registered before and after the model serving their owners. Singleton work carries a placement
      filter: `AddStrataraOrleans` publishes the silo's registered works in its silo metadata, and the director keeps
      only silos that name the work; every registration of the model adds the filter, because a silo that lacks it
      cannot place the grain (`CoHostingTests` found this). `SingletonPlacementTests` (two silos, eight works, all on
      the registering silo) and `SingletonWorkTests` green. Deviation: "timer owners registered after the model fire"
      is verified as routing and start-check unit tests rather than on a silo; the firing path itself is covered by
      `OwnerCheckedTimerTests` and `HardKillTimerTests`, green with the enumerable resolution. A run of twelve classes
      at once exhausted the test container's connections for the two singleton tests; alone they pass (2/2).

## 5. Timers, processes, rebuild and reset (D17, D18, D19, D21, D22)

- [x] 5.1 The timer-owner grain is reentrant (D17). Verify: integration tests in which a fact and a timeout
      for one process collide, and in which `OnTimeoutAsync` reschedules — both complete, and the timeout
      fires once.
      Done: `TimerOwnerGrain` is `[Reentrant]`. `TimerReentrancyTests` covers both in one process: a fact step holds
      the process past the tick's due time and then cancels a timer while the tick waits, the first tick reschedules
      from inside `OnTimeoutAsync`, the second completes the process and cancels its timers; each step runs once
      and no timer remains. Green.
- [x] 5.2 A process step registers its timers before its append; the process contract documents it (D18).
      Verify: a kill test between the registration and the append in which the timeout fires once after
      the fact is applied again; `SagaProcessTimeoutTests` asserts a timer exists before the kill.
      Done: `SagaProcessGrain` cancels and registers a step's timers, then appends, then cancels all timers of a
      completed process (a kill before that leaves timers the owner check drops). The contract remark in
      `SagaProcess<TState>` already stated the order. The saga scenario gained a holding `IDurableTimers` decorator
      and `timers`/`expirations`/`hold-registrations` commands; the new kill test holds after the registration,
      kills with the timer stored and no process stream, and after the restart finds one expiry and no timer. The
      existing kill test waits until the timer exists before each kill. Green.
- [x] 5.3 The due-time tolerance option (D19). Verify: a unit test with a fake time provider in which a
      tick slightly before the due time fires.
      Done: `DurableTimerOptions.DueTolerance` (default 500 ms, validated at least zero and shorter than the retry
      period; a first default of 2 s failed every host with a 1 s retry period). A tick within it fires and reports
      the due time as its firing time. `TimerDueTimeTests` (3, `FakeTimeProvider`) green.
- [x] 5.4 The rebuilder resets checkpoints before truncating (D21). Verify: `ProjectionRebuilder` end to end
      against a real store, including a truncation that throws, after which the projection re-reads from
      the beginning.
      Done: reset, then truncate, then resume in `finally`. `ProjectionRebuilderTests` gained the order and the
      throwing truncation; `RebuildEndToEndTests` rebuilds a probe projection on PostgreSQL while its readers run —
      a truncation that empties the model and throws, then a successful one — and both refill every row without a
      new fact. The probe is inert in the other hosts of the assembly that discover their projections. Green.
- [x] 5.5 `IExecutionModelReset`, its storage implementation in the persistence package and the host's
      directory callback (D22). Verify: `ResetTests` run against the port instead of the test-only reset.
      Done: `IExecutionModelReset` and `ExecutionModelResetReport` in `Stratara.Orleans.Hosting`;
      `AddStrataraExecutionModelReset<TReadContext>(runtimeConnectionString, clearDirectory)` in the persistence
      package clears the reminders of the host's service and the membership (and its version row) of its cluster,
      as the cluster options name them, every checkpoint, and the directory through the callback. `ResetTests` runs
      the port from the stopped host with assertions scoped to its deployment; `PocReset` keeps only the Redis
      cleanup and the counts. Surface lists updated. Green.

## 6. Documentation and gates (D13, D26)

- [ ] 6.1 `docs/` pages: the capability; the migration page, with the portable reader's backfill and the
      full-replay note; the operations page, with the hard-death scenario and its three answers, the
      statement that returns a kept command, and the host code for a Redis-backed directory. Verify:
      `tests/Stratara.Documentation.Tests` green and every API the pages name exists.
- [ ] 6.2 `llms.txt` core facts; `CHANGELOG.md`
      unreleased entry; the package count 25 to 27 everywhere D13 lists; the log-event range; a
      cheatsheet row per new registration; the pin comment in `Directory.Packages.props`; `llms-full.txt`
      regenerated with `dotnet run --project tools/Stratara.ReferenceCatalogue -- llms-full.txt`. Verify:
      `ReferenceCatalogueIsCurrentTests`, `DiCheatsheetCoverageTests`, `LogEventAllocationTests` and
      `PublishFilterCoverageTests` green, and no page states 25 packages.
- [ ] 6.3 The coverage exclusion for `src/Stratara.Orleans/**` leaves `.github/workflows/sonar.yml`, and the
      analysis collects the Orleans integration suite's in-process coverage (D26). Verify: the first
      analysis of `main` after the merge reports the quality gate as passed with the package in the
      coverage measure.

- [ ] 6.4 `README.md` presents the execution model as the headline feature, after 7.3 so real numbers
      exist (D13): a door beside the existing three that leads with the benefit the capability
      specifies, a step in *It grows with you*, the measured B3 and B5 rows in *Numbers, not
      adjectives*, and the recommended model beside the supported bus workers. Verify: every number
      equals a value in `evidence/results.md` from 7.3; every link resolves; `LandingBadgeTests` green.
- [ ] 6.5 `docs/index.md`, the landing page, carries the same door, a `st-feature` card in *What is in
      the box*, and the same numbers (D13). Verify: `docfx build docs/docfx.json --warningsAsErrors`
      green; `SiteMetadataTests` green; the numbers identical to `README.md`.
- [ ] 6.6 `docs/getting-started/choose-an-execution-model.md`, derived from `orleans-execution` and
      `host-composition`, links the capability, migration and operations pages and is listed in its
      `toc.yml` (D13). Verify: `tests/Stratara.Documentation.Tests` green.
- [ ] 6.7 The minor's `CHANGELOG.md` section opens with the execution model, since `release.yml` →
      `announce` publishes that section as the GitHub release note (D13). Verify: a read of the section
      before the tag.

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
