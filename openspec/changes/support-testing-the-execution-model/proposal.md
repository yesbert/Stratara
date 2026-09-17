# Support testing the execution model

> **Status:** approved (owner, 2026-09-17 — recorded at the owner's request)

## Why

The round-5 audit (R5-Mig-008) asked how a team that adopts the Orleans execution model tests its own
handlers, projections, sagas and timers on it, and found no answer in the framework:

- **The test-support packages know nothing of the execution model.** `Stratara.Testing` and
  `Stratara.Testing.EntityFrameworkCore` cover the aggregate harness, the in-memory doubles and the
  real write stack on SQLite; nothing runs a command in its aggregate's activation, a projection from
  a checkpoint, or a durable timer. A consumer who wants a test on the model copies the framework's own
  `CoHostingTests` — Testcontainers for PostgreSQL, Redis and RabbitMQ, `UseLocalhostClustering` on
  fixed ports, `UseInMemoryReminderService`, `MinimumReminderPeriod = 1 s` — and inherits its port
  collisions and its two-minute start.
- **The one testing dependency the framework declares is unused.** `Microsoft.Orleans.TestingHost` is
  referenced by the integration tests and `TestCluster` is never used; it neither guides a consumer nor
  tests anything.
- **The testing guide has no Orleans section, and no sample runs the model.** `docs/guides/testing-patterns.md`
  ends with the SQLite host; `samples/` runs nine samples and none touches `Stratara.Orleans`, so
  nothing in the repository shows a command reaching a grain, a projection reading the store or a timer
  firing, and nothing is smoke-tested for it.

## What Changes

- **A new test-support package, `Stratara.Testing.Orleans`,** offers `ExecutionModelTestHost`: one
  silo in the test's process, clustered with itself on free ports, with in-memory reminders, an
  in-memory grain directory under the name the model selects, the real write stack on in-memory SQLite
  from `Stratara.Testing.EntityFrameworkCore`, the portable commit-order reader and the checkpoint
  store on the same database, and every period the model keeps as a reminder or a poll shortened to
  seconds — so that a test dispatches a command, waits for the readers to catch up, registers a timer
  and sees it fire, within seconds and without Docker. The host exposes the session provider, the
  timers, the seeding and a reset over what it keeps, and a wait that returns once every registered
  store reader has reached the store's head. The consumer's roles are registered through the same
  calls as in production — `AddStrataraAggregateGrains`, `AddStrataraProjectionGrains`,
  `AddStrataraSagaGrains`, `AddStrataraDurableTimers`, `AddStrataraOrleansCommandDispatcher` — on the
  host's service collection, so a test proves the production registration, not a substitute.
- **The package keeps the test-support boundary:** the build-time reference check and the runtime
  environment guard of the other two packages apply to it.
- **The testing guide gains an Orleans section**, the package a README, the package pages a row, and
  the migration guide a pointer; the unused `Microsoft.Orleans.TestingHost` reference is dropped.
- **A sample, `Stratara.Sample.OrleansExecutionModel`,** runs a command into its aggregate's
  activation, a projection that reads the store, and a process timeout, on the test host, in one
  console run, and is smoke-tested like every other sample.
- **Consumer-visible effects:** a new packable package — the family grows from 27 to 28 —, no change
  to any existing package's behaviour. Versioning: minor (a new packable csproj; the house rule for
  additive members is patch, for a new package minor). Closes R5-Mig-008.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `test-support`: new requirement *A test can run the execution model in one process* — the in-process
  host, what it shortens, what it exposes, and that a consumer's roles are registered as in production;
  *Test-support packages are for test projects* — the third package, and the runtime guard on the
  execution-model composition; new scenarios.
- `orleans-execution`: *The execution model can be adopted per role beside the bus workers* — the
  documentation shows how a consumer tests on the model in one process; new scenario.

## Impact

- New: `src/Stratara.Testing.Orleans/` (`ExecutionModelTestHost`, `ExecutionModelTestHostOptions`,
  `StrataraTestReadDbContext`, the in-memory grain directory, the reset over the host's stores, the
  `build/Stratara.Testing.Orleans.targets` reference check, `README.md`), listed in
  `Stratara.Publish.slnf`; `tests/Stratara.Testing.Orleans.Tests/`; `samples/Stratara.Sample.OrleansExecutionModel/`
  with `tests/Stratara.Samples.SmokeTests/OrleansExecutionModelSampleSmokeTests.cs`.
- `Stratara.Testing.EntityFrameworkCore` — `AddStrataraTestingEventStore` accepts the write context's
  interceptors, so the partition counter can be added without a second factory; the environment guard
  is shared with the new package.
- `Stratara.Orleans` — `InternalsVisibleTo` for the new package's tests only if a test needs it;
  the host itself uses published members only.
- `tests/Stratara.Orleans.IntegrationTests/Stratara.Orleans.IntegrationTests.csproj` — the unused
  `Microsoft.Orleans.TestingHost` reference removed; `Directory.Packages.props` keeps the pin only if
  the new package uses it.
- Documentation: `docs/guides/testing-patterns.md` (an *On the Orleans execution model* section),
  `docs/guides/migrate-to-the-orleans-execution-model.md` (a pointer under *Adopt the roles*),
  `docs/overview/packages.md`, `docs/overview/architecture-at-a-glance.md`, `samples/README.md`,
  `README.md` (package map), `llms.txt`, `CHANGELOG.md`; the tier diagram in the agent-context
  repository, which the packable-projects checklist asks for; every "27 packages" becomes "28":
  `README.md`, `CONTRIBUTING.md`, `CHANGELOG.md` (header), `docs/index.md`, `docs/overview/what-is-stratara.md`,
  `docs/overview/index.md`, `docs/overview/packages.md`, `docs/overview/architecture-at-a-glance.md`,
  `.github/copilot-instructions.md`, `openspec/config.yaml`, `llms.txt`.
- Versioning: minor (4.2.0).
- Out of scope, for the owner: an execution-model slice in the `yesbert/Stratara.Examples` repository,
  which consumes the published packages and can only follow the release that ships this one.
