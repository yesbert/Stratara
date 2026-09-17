## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. The package (D1)

- [ ] 1.1 `src/Stratara.Testing.Orleans/Stratara.Testing.Orleans.csproj`: packable, `PackageId`, description,
      tags (`testing;orleans;in-process;event-sourcing;cqrs;dotnet;stratara`), references
      (`Stratara.Orleans`, `Stratara.Orleans.EntityFrameworkCore`, `Stratara.Testing.EntityFrameworkCore`,
      `Microsoft.Orleans.Server`, `Microsoft.Extensions.Hosting`), README/icon/licence pack items,
      `build/Stratara.Testing.Orleans.targets` with the `STRATARA1001` check; listed in `Stratara.Publish.slnf`.
      Verify: `dotnet pack src/Stratara.Testing.Orleans -c Release` produces a nuspec whose Stratara dependencies
      are all packable; the slnf entry.
- [ ] 1.2 `tests/Stratara.Testing.Orleans.Tests/` with the build-time and runtime guard tests mirrored from
      `tests/Stratara.Testing.EntityFrameworkCore.Tests/TestSupportEnvironmentGuardTests.cs` (scenario *The
      test execution-model host is created under a stated environment*). Verify: the tests; the shared guard.

## 2. The host (D2)

- [ ] 2.1 `AddStrataraTestingEventStore` gains an optional `Action<DbContextOptionsBuilder>? configureContext`
      applied after `UseSqlite`; existing callers unchanged. Verify:
      `src/Stratara.Testing.EntityFrameworkCore/TestEventStoreServiceCollectionExtensions.cs`; a test in
      `tests/Stratara.Testing.EntityFrameworkCore.Tests` that adds an interceptor through it.
- [ ] 2.2 `InMemoryGrainDirectory` (internal) honouring register-returns-existing, unregister-matching-only,
      lookup and unregister-silos; unit tests for each. Verify: `src/Stratara.Testing.Orleans/InMemoryGrainDirectory.cs`;
      `tests/Stratara.Testing.Orleans.Tests/InMemoryGrainDirectoryTests.cs`.
- [ ] 2.3 `StrataraTestReadDbContext`, `ExecutionModelTestHostOptions` (the periods with the defaults D2 names)
      and `ExecutionModelTestHost` (`CreateAsync`, `Services`, `Session`, `Timers`, `DispatchAsync`,
      `SeedAtHeadAsync`, `WaitForReadersAsync`, `DisposeAsync`), free-port selection, the guard call, the
      `PostConfigure` of the periods, the drain registered. Verify: the files; scenario tests
      *A command runs in its aggregate's activation and a projection applies it*, *A timer fires within
      seconds*, *A process timeout fires* and *A test shortens a period below the runtime's default* in
      `tests/Stratara.Testing.Orleans.Tests/ExecutionModelTestHostTests.cs`, each finishing in under ten seconds.
- [ ] 2.4 The portable reader and the counter interceptor on SQLite: a test that appends across partitions and
      reads every entry back in position order through the host's reader. Verify:
      `tests/Stratara.Testing.Orleans.Tests/SqlitePortableReaderTests.cs`; the reader's XML remark and the
      migration guide's "verified on PostgreSQL only" sentence extended.

## 3. Seeding and reset (D3)

- [ ] 3.1 `ResetAsync` and the package's `IExecutionModelReset` over the in-memory reminder table, the directory
      and the registered consumers' checkpoints, with counts. Verify: `src/Stratara.Testing.Orleans/InMemoryExecutionModelReset.cs`;
      scenario *A test seeds and resets* in `ExecutionModelTestHostTests.cs` (seeded start applies nothing old;
      after the reset no checkpoint and no timer remains).

## 4. Documentation and sample (D4)

- [ ] 4.1 `docs/guides/testing-patterns.md` section *On the Orleans execution model*; the pointer in
      `docs/guides/migrate-to-the-orleans-execution-model.md` under *Adopt the roles*; the package README.
      Verify: the sections; documentation tests (the snippets compile against the package).
- [ ] 4.2 `samples/Stratara.Sample.OrleansExecutionModel/` (command into its activation, projection from the
      store, process timeout) with `StrataraAllowTestSupportOutsideTests`, and
      `tests/Stratara.Samples.SmokeTests/OrleansExecutionModelSampleSmokeTests.cs` asserting the three output
      lines; `samples/README.md` row and the coverage sentence. Verify: the smoke test green in the gauntlet.
- [ ] 4.3 The package count 27 → 28 and the package rows: `README.md`, `CONTRIBUTING.md`, `CHANGELOG.md` header,
      `docs/index.md`, `docs/overview/what-is-stratara.md`, `docs/overview/index.md`, `docs/overview/packages.md`,
      `docs/overview/architecture-at-a-glance.md`, `.github/copilot-instructions.md`, `openspec/config.yaml`,
      `llms.txt` (and `llms-full.txt` regenerated); the tier diagram in the agent context repository.
      Verify: `grep -rn "27 " --include='*.md' --include='*.yaml' --include='*.txt' .` shows no package count left.
- [ ] 4.4 `tests/Stratara.Orleans.IntegrationTests/Stratara.Orleans.IntegrationTests.csproj` drops
      `Microsoft.Orleans.TestingHost`; `Directory.Packages.props` drops the pin if nothing else uses it.
      Verify: the csproj; `dotnet build tests/Stratara.Orleans.IntegrationTests`.
- [ ] 4.5 `CHANGELOG.md` `[Unreleased]` *Added* (the package, the host, the sample) and *Changed*
      (`AddStrataraTestingEventStore`'s parameter, the reader's SQLite note). Verify: the entries.

## 5. Close

- [ ] 5.1 `./scripts/local-gauntlet.sh` green, including the new test project, the sample smoke test and the
      pack of the new package. Verify: the run output, recorded here.
- [ ] 5.2 `openspec validate support-testing-the-execution-model --strict` passes. Verify: the output.
