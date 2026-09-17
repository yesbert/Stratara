## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.
      *Done:* approved (owner, 2026-09-17).

## 1. The package (D1)

- [x] 1.1 `src/Stratara.Testing.Orleans/Stratara.Testing.Orleans.csproj`: packable, `PackageId`, description,
      tags (`testing;orleans;in-process;event-sourcing;cqrs;dotnet;stratara`), references
      (`Stratara.Orleans`, `Stratara.Orleans.EntityFrameworkCore`, `Stratara.Testing.EntityFrameworkCore`,
      `Microsoft.Orleans.Server`, `Microsoft.Extensions.Hosting`), README/icon/licence pack items,
      `build/Stratara.Testing.Orleans.targets` with the `STRATARA1001` check; listed in `Stratara.Publish.slnf`.
      Verify: `dotnet pack src/Stratara.Testing.Orleans -c Release` produces a nuspec whose Stratara dependencies
      are all packable; the slnf entry.
      *Done:* the packed nuspec depends on `Stratara.Orleans`, `Stratara.Orleans.EntityFrameworkCore`, `Stratara.Testing.EntityFrameworkCore` (all packable), `Microsoft.Extensions.Hosting` and `Microsoft.Orleans.Server [10.3.1, 11.0.0)`, and carries `build/Stratara.Testing.Orleans.targets` and the README. Also listed in `Stratara.slnx`. `Stratara.Orleans` gains `InternalsVisibleTo` for the package, because the consumer names of the registered store readers — which the wait needs — are known only to the internal wake-up targets, which the seeding and the shipped reset read as well; the saga consumer's name is an internal constant.

- [x] 1.2 `tests/Stratara.Testing.Orleans.Tests/` with the build-time and runtime guard tests mirrored from
      `tests/Stratara.Testing.EntityFrameworkCore.Tests/TestSupportEnvironmentGuardTests.cs` (scenario *The
      test execution-model host is created under a stated environment*). Verify: the tests; the shared guard.
      *Done:* the guard stays in `Stratara.Testing.EntityFrameworkCore`, shared through `InternalsVisibleTo` (the smaller diff), with the refusal's description of what the composition wires as a parameter. `tests/Stratara.Testing.Orleans.Tests/ExecutionModelTestHostGuardTests.cs`: both variables refused naming the environment, the entry point and that the host runs the model in memory; the unstated case passes; the targets file's `STRATARA1001` condition and text.

## 2. The host (D2)

- [x] 2.1 `AddStrataraTestingEventStore` gains an optional `Action<DbContextOptionsBuilder>? configureContext`
      applied after `UseSqlite`; existing callers unchanged. Verify:
      `src/Stratara.Testing.EntityFrameworkCore/TestEventStoreServiceCollectionExtensions.cs`; a test in
      `tests/Stratara.Testing.EntityFrameworkCore.Tests` that adds an interceptor through it.
      *Done:* deviation: not an optional parameter on the existing overload — that would change a published method's signature, a binary break for a compiled caller — but a new overload that takes a connection string and `Action<DbContextOptionsBuilder>? configureContext`. It takes a connection string because one `SqliteConnection` shared by every context is not safe to use from several threads, and a silo's grains, readers and drain use the store at once; each context opens its own connection to a named shared-cache in-memory database the host keeps open. Test: `tests/Stratara.Testing.EntityFrameworkCore.Tests/ConfigureContextTests.cs` (an interceptor added through the overload runs on the write stack's save).

- [x] 2.2 `InMemoryGrainDirectory` (internal) honouring register-returns-existing, unregister-matching-only,
      lookup and unregister-silos; unit tests for each. Verify: `src/Stratara.Testing.Orleans/InMemoryGrainDirectory.cs`;
      `tests/Stratara.Testing.Orleans.Tests/InMemoryGrainDirectoryTests.cs`.
      *Done:* four tests: register returns the existing address, register replaces the named previous address, unregister removes only the matching address, unregister-silos and the clear's count.

- [x] 2.3 `StrataraTestReadDbContext`, `ExecutionModelTestHostOptions` (the periods with the defaults D2 names)
      and `ExecutionModelTestHost` (`CreateAsync`, `Services`, `Session`, `Timers`, `DispatchAsync`,
      `SeedAtHeadAsync`, `WaitForReadersAsync`, `DisposeAsync`), free-port selection, the guard call, the
      `PostConfigure` of the periods, the drain registered. Verify: the files; scenario tests
      *A command runs in its aggregate's activation and a projection applies it*, *A timer fires within
      seconds*, *A process timeout fires* and *A test shortens a period below the runtime's default* in
      `tests/Stratara.Testing.Orleans.Tests/ExecutionModelTestHostTests.cs`, each finishing in under ten seconds.
      *Done:* the host also registers the mediator, the resilience pipelines, the projection replay state and the projection and saga runtimes — what the worker composites would, without the bus — and `ExecutionModelTestHostOptions` gains `PartitionCount` (4, applied in every case, because the counter interceptor is built with it) and `BeforeStart` (see 3.1). `DispatchAsync` resolves the mediator in a scope whose session provider is its own, preset to `Session`: every scope, the grains' included, gets a provider of its own, as production's scoped provider does, so a grain setting the session for one entry does not change another's. The drain is registered with `OutboxDrainWork.WorkName`. Five scenario tests in `ExecutionModelTestHostTests.cs`, the class finishing in about five seconds. **Two framework defects surfaced and were fixed:** (a) a silo that started its store readers when it became active could fail its start with *No silo of the cluster registered the projections role*, because its own metadata had not reached its own cache — both placement directors now judge the local silo by the entries it publishes (`RolePlacement.cs`, `SingletonWorkPlacement.cs`); (b) a nudge that cannot be sent — before the silo has started — threw from the bundle dispatcher after the commit, contrary to its own "a lost nudge costs latency"; it is now caught (`OrleansEventBundleDispatcher.cs`, `tests/Stratara.Orleans.Tests/LostNudgeTests.cs`).

- [x] 2.4 The portable reader and the counter interceptor on SQLite: a test that appends across partitions and
      reads every entry back in position order through the host's reader. Verify:
      `tests/Stratara.Testing.Orleans.Tests/SqlitePortableReaderTests.cs`; the reader's XML remark and the
      migration guide's "verified on PostgreSQL only" sentence extended.
      *Done:* 24 streams of two entries across four partitions read back in batches of five: positions 1..n without a gap in every partition, the head equal to the last, each stream's versions in position order.

## 3. Seeding and reset (D3)

- [x] 3.1 `ResetAsync` and the package's `IExecutionModelReset` over the in-memory reminder table, the directory
      and the registered consumers' checkpoints, with counts. Verify: `src/Stratara.Testing.Orleans/InMemoryExecutionModelReset.cs`;
      scenario *A test seeds and resets* in `ExecutionModelTestHostTests.cs` (seeded start applies nothing old;
      after the reset no checkpoint and no timer remains).
      *Done:* deviation: the seeding scenario needs the history appended and seeded before the silo starts — a started host's readers apply it first — so `ExecutionModelTestHostOptions.BeforeStart` runs after the schema and before the start, as a deployment seeds while no silo runs. The reset clears the in-memory reminder table (`TestOnlyClearTable`, counting its rows first), the directory and the registered consumers' checkpoints.

## 4. Documentation and sample (D4)

- [x] 4.1 `docs/guides/testing-patterns.md` section *On the Orleans execution model*; the pointer in
      `docs/guides/migrate-to-the-orleans-execution-model.md` under *Adopt the roles*; the package README.
      Verify: the sections; documentation tests (the snippets compile against the package).
      *Done:* the section also names the fixture pattern and the periods; the snippet uses the documentation placeholders so the snippet check compiles it, and `Stratara.Documentation.Tests` references the package.

- [x] 4.2 `samples/Stratara.Sample.OrleansExecutionModel/` (command into its activation, projection from the
      store, process timeout) with `StrataraAllowTestSupportOutsideTests`, and
      `tests/Stratara.Samples.SmokeTests/OrleansExecutionModelSampleSmokeTests.cs` asserting the three output
      lines; `samples/README.md` row and the coverage sentence. Verify: the smoke test green in the gauntlet.
      *Done:* the sample prints the scheduler the handler ran on (`ActivationTaskScheduler`), the projected balance formatted invariantly, and the welcome; the smoke test allows 120 s. `samples/README.md` corrects the sentence on which packages the samples reference and that every sample runs in under a second.

- [x] 4.3 The package count 27 → 28 and the package rows: `README.md`, `CONTRIBUTING.md`, `CHANGELOG.md` header,
      `docs/index.md`, `docs/overview/what-is-stratara.md`, `docs/overview/index.md`, `docs/overview/packages.md`,
      `docs/overview/architecture-at-a-glance.md`, `.github/copilot-instructions.md`, `openspec/config.yaml`,
      `llms.txt` (and `llms-full.txt` regenerated); the tier diagram in the agent context repository.
      Verify: `grep -rn "27 " --include='*.md' --include='*.yaml' --include='*.txt' .` shows no package count left.
      *Done:* also `.github/workflows/release.yml`'s header comment and `README.md`'s Orleans door (a line pointing at the package and the sample); the tier diagram in `docs/overview/architecture-at-a-glance.md` gains the test-support block. The agent context repository's tier layout and package count are updated and pushed. The grep finds no package count of 27 outside the archive and this change's own artifacts.

- [x] 4.4 `tests/Stratara.Orleans.IntegrationTests/Stratara.Orleans.IntegrationTests.csproj` drops
      `Microsoft.Orleans.TestingHost`; `Directory.Packages.props` drops the pin if nothing else uses it.
      Verify: the csproj; `dotnet build tests/Stratara.Orleans.IntegrationTests`.
      *Done:* no other project used the package; the pin is dropped from `Directory.Packages.props`; the integration tests build.

- [x] 4.5 `CHANGELOG.md` `[Unreleased]` *Added* (the package, the host, the sample) and *Changed*
      (`AddStrataraTestingEventStore`'s parameter, the reader's SQLite note). Verify: the entries.
      *Done:* *Changed* also records the two framework fixes of 2.3.

## 5. Close

- [x] 5.1 `./scripts/local-gauntlet.sh` green, including the new test project, the sample smoke test and the
      pack of the new package. Verify: the run output, recorded here.
      *Done:* gauntlet green: build of the full solution, `Stratara.Testing.Orleans.Tests` 14 of 14, `Stratara.Samples.SmokeTests` 16 of 16 with the new sample, the documentation checks, and the pack of `Stratara.Testing.Orleans`. Because the placement and nudge fixes touch every silo, the `Hosting`, `Singleton`, `Projections` and `Sagas` integration namespaces ran against PostgreSQL, Redis and RabbitMQ: 43 of 43.

- [x] 5.2 `openspec validate support-testing-the-execution-model --strict` passes. Verify: the output.
      *Done:* valid.

