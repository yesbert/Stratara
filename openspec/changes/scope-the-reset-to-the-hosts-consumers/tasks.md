## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.
      Done: approved by the owner on 2026-09-16, recorded at the owner's request.

## 1. One source of the host's consumer names

- [ ] 1.1 `Stratara.Orleans` offers an internal service that yields the consumer names of the host's
      store readers: each `INudgeTarget` exposes the names it addresses — the projection names for
      `ProjectionNudgeTarget`, the saga consumer for `SagaNudgeTarget`. Verify: a unit test in
      `tests/Stratara.Orleans.Tests` that registers two projections with and without saga grains and
      asserts the names, with the saga consumer present only when saga grains are registered.
- [ ] 1.2 `ReplayCheckpointResetTruncator` takes its projection names from that service instead of
      enumerating `IProjection` itself. Verify: `tests/Stratara.Orleans.Tests/ReplayCheckpointResetTests.cs`
      passes unchanged.
- [ ] 1.3 `src/Stratara.Orleans/Stratara.Orleans.csproj` grants `InternalsVisibleTo` to
      `Stratara.Orleans.EntityFrameworkCore`. Verify: the csproj; the build.

## 2. Scope the reset

- [ ] 2.1 `ExecutionModelReset` deletes only the checkpoints whose consumer is one of those names, and
      reports that count; with no names it deletes none. Verify: `src/Stratara.Orleans.EntityFrameworkCore/Hosting/ExecutionModelReset.cs`.
- [ ] 2.2 The reset integration test writes a checkpoint of a projection the host does not register
      beside the host's own, and asserts that it survives the reset with its position while the host's
      checkpoints are gone and counted. Verify:
      `tests/Stratara.Orleans.IntegrationTests/Hosting/ResetTests.cs`, run against PostgreSQL.
- [ ] 2.3 The test's existing probe checkpoint uses a consumer name the host registers, or the
      assertion on the removed count is adjusted to what the host registers. Verify: the same test.
- [ ] 2.4 The XML documentation of `IExecutionModelReset` and `ExecutionModelResetReport.Checkpoints`
      says whose checkpoints are removed. Verify: `src/Stratara.Orleans/Hosting/IExecutionModelReset.cs`.

## 3. Documentation

- [ ] 3.1 `docs/guides/operate-the-orleans-execution-model.md` → *Reset what the model keeps* says the
      reset removes the checkpoints of the projections and sagas the host registers, must run from the
      host's own composition, and leaves a removed projection's checkpoints in place, inert.
      Verify: a read of that section; documentation tests.
- [ ] 3.2 Where the documentation configures the read store for the execution model
      (`AddStrataraProjectionCheckpoints`), it states the sharing limit: distinct consumer names, and at
      most one deployment running store-reading sagas per read store. Verify:
      `grep -rn "AddStrataraProjectionCheckpoints" docs` and a read of each hit's section.
- [ ] 3.3 `CHANGELOG.md` `[Unreleased]` → *Changed* names the narrower reset and the sharing limit.
      Verify: the entry.

## 4. Close

- [ ] 4.1 `./scripts/local-gauntlet.sh` green; the `Hosting` and `Projections` integration namespaces
      green against PostgreSQL. Verify: the run output, recorded here.
- [ ] 4.2 `openspec validate scope-the-reset-to-the-hosts-consumers --strict` passes. Verify: the output.
