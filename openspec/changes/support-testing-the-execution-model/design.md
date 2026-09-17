## Context

See `proposal.md` — Why. The current code:

- **The test-support packages.** `Stratara.Testing` (`src/Stratara.Testing/Stratara.Testing.csproj`)
  references `Stratara.Abstractions`, `Contracts`, `Shared` and `Security` — nothing of Tier-C — and
  ships the harness and the doubles. `Stratara.Testing.EntityFrameworkCore` is the per-concern split:
  it references `Stratara.Infrastructure`, `Stratara.EventSourcing.EntityFrameworkCore` and
  `Stratara.Testing`, and `EventStoreTestHost.Create` (`EventStoreTestHost.cs:70-84`) opens a
  `Filename=:memory:` SQLite connection, calls `AddStrataraTestingEventStore<TWriteDbContext>`
  (`TestEventStoreServiceCollectionExtensions.cs:54-87`) — which registers the SQLite factory with a
  fixed `UseSqlite(sharedConnection)` callback and no hook for interceptors (`:71-73`) — and builds a
  `ServiceProvider`. `StrataraTestWriteDbContext` derives from the framework's `WriteDbContext<T>`
  (`StrataraTestWriteDbContext.cs:13-14`), so it carries the execution model's schema additions
  (`partition_position`, the counter table, the outbox columns). `TestSupportEnvironmentGuard` is
  internal (`TestSupportEnvironmentGuard.cs:6`). Both packages carry a `build/*.targets` that fails a
  non-test project with `STRATARA1001` (`src/Stratara.Testing/build/Stratara.Testing.targets`).
- **What a silo needs.** `AddStrataraOrleans` on the silo builder registers the durable directory
  under `GrainDirectories.Durable` and the placement filters; `DurableDirectoryCheck` fails the silo
  when no `IGrainDirectory` is keyed by that name (`DurableDirectoryCheck.cs:33-43`). The store readers
  need an `ICommittedPositionReader` and an `IProjectionCheckpointStore`;
  `AddStrataraPortableCounterReader<TWrite>` and `AddStrataraProjectionCheckpoints<TRead>` register
  them (`OrleansCommitOrderServiceCollectionExtensions.cs:38`, `OrleansCheckpointServiceCollectionExtensions.cs:28`)
  and the read context derives from `ReadDbContext<T>` (`ReadDbContext.cs:20`). The portable reader and
  the counter interceptor go through the EF model, not SQL text (`PartitionCounterInterceptor.cs:138`),
  and are verified on PostgreSQL only (`PortableCounterReader.cs:22`). Every period that is a reminder
  — `KeepAlivePeriod`, `RetryPeriod` — is validated against `ReminderOptions.MinimumReminderPeriod`
  (`OrleansOptionsValidator`), which `CoHostingTests.cs:118` lowers to one second.
- **The ports the host exposes are public.** `IStoreReaderSeeding` (`Hosting/IStoreReaderSeeding.cs`),
  `IExecutionModelReset` (`Hosting/IExecutionModelReset.cs`), `IDurableTimers`,
  `ICommittedPositionReader.HeadAsync` and `IProjectionCheckpointStore.GetAsync` are all published;
  a wait for the readers needs nothing internal.
- **The framework's own tests.** `CoHostingTests.cs:109-120` is the in-process silo shape:
  `UseLocalhostClustering(siloPort, gatewayPort)` on fixed ports, Redis for both directories,
  `UseInMemoryReminderService`, the minimum reminder period at one second; `LongHandlerTimerTests.cs:55-72`
  the same with PostgreSQL reminders. `Stratara.Orleans.IntegrationTests.csproj` references
  `Microsoft.Orleans.TestingHost`; no file uses `TestCluster`.
- **Samples.** `samples/README.md` lists nine self-contained samples referencing four packages;
  `tests/Stratara.Samples.SmokeTests/SampleRunner.RunUntilExit` runs each as a subprocess and asserts
  on its output. `samples/Directory.Build.props` suppresses CS1591. The test-support targets offer
  `StrataraAllowTestSupportOutsideTests` for "a sample, a benchmark".

## Goals / Non-Goals

**Goals:**
- A consumer test on the execution model is one `await using var host = await ExecutionModelTestHost.CreateAsync(...)`
  away, runs in seconds, needs no Docker, and registers the consumer's roles with the production calls.
- The package sits where the other test-support packages sit and is guarded the same way.
- The testing guide, a README and a sample show it; the integration suite stops declaring a testing
  dependency it does not use.

**Non-Goals:**
- Multi-silo tests in memory. Membership, reminders and the directory in memory cannot be shared by
  two silos; a kill, a role split or a takeover is an integration test against real infrastructure,
  as the framework's own are.
- A test double for the grains. The host runs the real grains; the test proves the production
  registration.
- Replacing the framework's own integration tests with the host. They test the model against
  PostgreSQL, Redis and RabbitMQ and stay.
- An execution-model slice in `Stratara.Examples`. That repository consumes published packages; the
  slice follows the release, as its own work there.

## Decisions

### D1 — A third test-support package, `Stratara.Testing.Orleans`, not a growth of `Stratara.Testing`

`Stratara.Testing` references nothing of Tier-C; adding `Stratara.Orleans`, `Stratara.Orleans.EntityFrameworkCore`,
`Stratara.Testing.EntityFrameworkCore` and the Orleans runtime to it would pull the Orleans runtime
and SQLite into every consumer test project that wants the aggregate harness alone — the reason
`Stratara.Testing.EntityFrameworkCore` is its own package. `Stratara.Testing.Orleans` references
`Stratara.Orleans`, `Stratara.Orleans.EntityFrameworkCore`, `Stratara.Testing.EntityFrameworkCore`
(and through it `Stratara.Testing`), `Microsoft.Orleans.Server` for the in-process silo, and
`Microsoft.Extensions.Hosting`. It is packable, listed in `Stratara.Publish.slnf`, carries the
README, icon and licence, the `build/Stratara.Testing.Orleans.targets` reference check with the same
`STRATARA1001` code and message shape, and every public member is documented. Versioning: a new
packable csproj is a minor bump by the house rule; the additive-member rule does not cover a package.

*Rejected: extending `Stratara.Testing`.* Above.
*Rejected: extending `Stratara.Testing.EntityFrameworkCore`.* It would make the SQLite write stack
depend on the Orleans runtime for a consumer who tests without the execution model.

Evidence: `Stratara.Testing.csproj` (references), `Stratara.Testing.EntityFrameworkCore.csproj`
(the split's precedent), the packable-projects conventions in `openspec/config.yaml` → `context`.

### D2 — `ExecutionModelTestHost`: one silo, in memory, on free ports, on the SQLite write stack

`ExecutionModelTestHost.CreateAsync(Action<IServiceCollection>? configure = null, Action<ExecutionModelTestHostOptions>? options = null)`
builds an `IHost`:

- `UseOrleans`: `UseLocalhostClustering` on two free ports the host picks by binding and releasing
  ephemeral ports (fixed ports are what makes the framework's own tests collide);
  `UseInMemoryReminderService`; `ReminderOptions.MinimumReminderPeriod` set to the options' floor;
  `AddStrataraOrleans((s, name) => s.AddKeyedSingleton<IGrainDirectory>(name, InMemoryGrainDirectory))`
  with the package's own `InMemoryGrainDirectory` — a dictionary keyed by grain id that honours the
  contract for one silo: a registration returns the address already registered where one exists,
  an unregistration removes only the matching address, `UnregisterSilos` removes every address of the
  silos named; a `ClusterId` and `ServiceId` unique per host.
- The store: `AddStrataraTestingEventStore<StrataraTestWriteDbContext>` on a `Filename=:memory:`
  connection the host owns, with the `PartitionCounterInterceptor` added through a new optional
  `Action<DbContextOptionsBuilder>` on `AddStrataraTestingEventStore` (the one edit to the existing
  package); `StrataraTestReadDbContext : ReadDbContext<StrataraTestReadDbContext>` on the same
  connection, `AddStrataraProjectionCheckpoints<StrataraTestReadDbContext>`,
  `AddStrataraPortableCounterReader<StrataraTestWriteDbContext>`, `AddStrataraIntentStore<StrataraTestWriteDbContext>`,
  schema created with `EnsureCreated` on both contexts.
- The periods, from `ExecutionModelTestHostOptions` with these defaults: `MinimumReminderPeriod` and
  every keep-alive and retry period 1 s, `PollInterval` 250 ms, `IntentGrace` 2 s, `DueTolerance` at
  its default, `OutboxDrainOptions.PollingInterval` 1 s. The host applies them with `Configure<T>`
  through `PostConfigure<T>` that sets a value only where the option still holds its framework
  default, so that a value a test set in `configure` — through a registration call's `Action<Options>`
  or `Configure<T>` — wins.
- The consumer's `configure` runs against the host's `IServiceCollection` and registers handlers,
  projections, sagas, timer ports and the roles with the production calls; the host registers
  `AddStrataraSingletonWork<OutboxDrainWork>()` itself so recorded commands are resumed.
- Exposed: `Services`, `Session` (the `TestSessionContextProvider`), `Timers` (`IDurableTimers`),
  `DispatchAsync(command)` (a scope with the session set, through `IMediator`),
  `SeedAtHeadAsync()` (the real `IStoreReaderSeeding`), `ResetAsync()` (D3),
  `WaitForReadersAsync(TimeSpan? timeout)` — polls, for every consumer name the host's nudge targets
  know and every partition, `IProjectionCheckpointStore.GetAsync` against `ICommittedPositionReader.HeadAsync`
  until every checkpoint is at or past the head, and fails with the lagging consumer and partition on
  timeout. The consumer names are read from the registered projections through `IProjectionHandler.GetProjectionName`
  and the saga consumer name; both are public. `DisposeAsync` stops the host and closes the connection.
- The environment guard: `TestSupportEnvironmentGuard` becomes shared (moved to `Stratara.Testing`
  as internal with `InternalsVisibleTo` for both EF packages, or duplicated — the apply decides by the
  smaller diff) and `CreateAsync` calls it with its own entry-point name.

*Rejected: `TestCluster` / `InProcessTestCluster` from `Microsoft.Orleans.TestingHost`.* Both host
silos in the process, but they own the host builder, the logging and the client; the store's SQLite
connection, the doubles and the consumer's registrations would have to be threaded through their
configurators, and a second silo — the thing they exist for — cannot share in-memory reminders or the
directory. One `IHost` with `UseOrleans` is what the framework's own tests use and what a consumer
already knows. The unused reference in the integration tests goes.
*Rejected: PostgreSQL through Testcontainers as the host's store.* It is what a consumer copies
today; the point is the test that runs without Docker.
*Rejected: the native PostgreSQL reader.* SQLite has no transaction ids; the portable reader is the
one that runs on any relational provider, and the counter interceptor is provider-neutral through the
model. Its "verified on PostgreSQL only" note gains "and on SQLite through the test host".

Evidence: `CoHostingTests.cs:109-120`; `EventStoreTestHost.cs:70-96`; `TestEventStoreServiceCollectionExtensions.cs:71-73`;
`DurableDirectoryCheck.cs:33-43`; `OrleansCommitOrderServiceCollectionExtensions.cs:38`;
`ProjectionGrain.cs:216-218` (how consumer names are derived); Orleans `IGrainDirectory` (Register,
Unregister, Lookup, UnregisterSilos). Tests: the five scenarios of the new requirement, in
`tests/Stratara.Testing.Orleans.Tests`.

### D3 — The reset on the host clears what the host keeps, through the model's own port

`ResetAsync()` resolves `IExecutionModelReset` from a scope, as the guide shows for production. The
package registers its own implementation: reminders through `IReminderTable.TestOnlyClearTable()`
(the in-memory table's clearing member), the directory through the in-memory directory's clear, the
checkpoints of the registered consumers through `IProjectionCheckpointStore` as the shipped reset
scopes them, membership left alone (one silo, its own row), and a report with the counts. A test that
wants "nothing remembered" may also dispose the host and create a new one; the README says the reset
exists so that a test of a consumer's reset procedure runs the port it will run in production.

*Rejected: reusing `AddStrataraExecutionModelReset`.* It deletes from the ADO.NET tables by name; the
host has none.

Evidence: `IExecutionModelReset.cs`; `ExecutionModelReset.cs` (the scoping to mirror); Orleans
`IReminderTable.TestOnlyClearTable`.

### D4 — The documentation and the sample

`docs/guides/testing-patterns.md` gains *On the Orleans execution model*: the package reference, the
host in eight lines (create, register a handler and a projection with the production calls, dispatch,
wait, assert), the timer case, the one-silo limit and the pointer to `*IntegrationTests` for the rest.
The migration guide's *Adopt the roles* ends with one sentence pointing there. The package README
follows the two existing ones. `docs/overview/packages.md`, `architecture-at-a-glance.md`,
`README.md`'s package map, `llms.txt` and every "27 packages" are updated together — the count is
stated in eleven files (the proposal lists them).

`samples/Stratara.Sample.OrleansExecutionModel`: a console sample on the test host with
`StrataraAllowTestSupportOutsideTests=true` — the documented exception for a sample — that opens an
account through a command (the handler runs in the aggregate's activation), waits for a balance
projection that reads the store, registers a process with a timeout of one second and prints when it
fires, then exits; output lines the smoke test asserts. `samples/README.md` adds it as the sixth
learning-path sample and corrects the sentence about which packages the samples cover.

*Rejected: a sample on PostgreSQL and a real silo.* It cannot run in the smoke tests without Docker
and would duplicate the migration guide.

Evidence: `docs/guides/testing-patterns.md:40-111`; `samples/README.md`; `SampleRunner.cs`;
`src/Stratara.Testing/build/Stratara.Testing.targets` (the opt-out).

## Risks / Trade-offs

- [The portable reader or the counter interceptor behaves differently on SQLite] → the package's
  tests run the reader against SQLite on every gauntlet; a difference fails there, not in a consumer.
- [Free-port selection races another process] → the ports are bound and released immediately before
  the silo starts; a collision fails the start visibly and the test reruns.
- [A test's own option values are overridden by the host's shortened periods] → the host applies its
  values with `PostConfigure` only where the option holds its framework default; a test that set a
  value keeps it.
- [The host takes seconds to start and a consumer creates one per test] → the README shows the xUnit
  class-fixture pattern with a reset between tests.
- [The in-memory directory is trusted beyond one silo] → it is internal to the package, and the host
  and README say one silo.
- [The package count changes in eleven places] → listed in the proposal's Impact; the doc-symbol and
  documentation tests do not check the number, so the task names each file.

## Migration Plan

Minor release (4.2.0). New package `Stratara.Testing.Orleans`; one optional parameter added to
`AddStrataraTestingEventStore`. No behaviour change in any runtime package. Rollback: the package is
not referenced by any other.
