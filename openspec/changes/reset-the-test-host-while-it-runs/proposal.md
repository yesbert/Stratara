# reset-the-test-host-while-it-runs

> **Status:** approved (owner, 2026-09-17 — recorded at the owner's request)

## Why

The review that replaced Copilot's on the pull requests merged on 2026-09-17 ran the test host's
reset the way its own README tells a test to: a host shared by a class, reset between tests. It does
not work, and it is the one thing the reset is for.

- **The reset deletes the checkpoints while the readers run.** A reader keeps the position it had
  cached, so either it never advances again and the wait for the readers times out — the checkpoint
  says nothing was applied, the store's head says otherwise — or it reads again from the beginning
  and applies the last test's facts a second time, into a read model the reset does not empty.
- **A start that fails leaves a silo running.** Only the database connection is closed; a silo that
  started part-way keeps its ports, its threads and its timers for the rest of the test run, and the
  noise buries the failure the test was about to report.
- **Two hosts created at once can be handed the same port.** The host asks the operating system for a
  free port and closes the listener before the silo binds it.
- **A placement filter can judge the silo by an empty table.** The silo's own metadata entries are
  written when the runtime first materialises its options, which nothing orders before the first
  placement.

## What Changes

- **The reset works on a running host.** It stops the registered readers, puts every one of them at
  the store's head, and starts them again: the next test's facts are applied, the last test's are not
  applied again, and the wait for the readers returns at once. What it reports is what it moved.
- **Readers can be stopped whatever they read.** The pause the projections had moves into the store
  reader every consumer shares, so a saga's reader stops for a reset like a projection's.
- **A start that fails stops what it started**, and a start refused because another host took the
  port is tried again with a port of its own.
- **A placement filter writes the silo's entries before it reads them.**

## Impact

- Affected specs: `test-support`
- Affected code: `src/Stratara.Testing.Orleans/InMemoryExecutionModelReset.cs`,
  `src/Stratara.Testing.Orleans/ExecutionModelTestHost.cs`,
  `src/Stratara.Orleans/Projections/StoreReaderGrain.cs`,
  `src/Stratara.Orleans/Projections/ProjectionGrain.cs`,
  `src/Stratara.Orleans/Sagas/SagaGrain.cs`,
  `src/Stratara.Orleans/Hosting/RolePlacement.cs`,
  `src/Stratara.Orleans/Singleton/SingletonWorkPlacement.cs`
- Affected docs: `src/Stratara.Testing.Orleans/README.md`, `docs/guides/testing-patterns.md`,
  `CHANGELOG.md`
- Editorial, outside the delta: `openspec/specs/package-distribution/spec.md` counted twenty-five
  packages in its purpose; it now counts none.
- No public API changes: `INudgeTarget` is internal and its two new members have defaults.
