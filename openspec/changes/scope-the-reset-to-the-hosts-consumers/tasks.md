## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.
      Done: approved by the owner on 2026-09-16, recorded at the owner's request.

## 1. One source of the host's consumer names

- [x] 1.1 Each `INudgeTarget` exposes the consumer names it addresses — the projection names for
      `ProjectionNudgeTarget`, the saga consumer for `SagaNudgeTarget`. Verify: a unit test in
      `tests/Stratara.Orleans.Tests` that composes projection grains with and without saga grains and
      asserts the union of names, with the saga consumer present only when saga grains are registered.
      Done: `ConsumerNames` on `INudgeTarget`; `StoreReaderConsumerNamesTests` (2 tests). Orleans unit tests 82/82.
- [x] 1.2 `ReplayCheckpointResetTruncator` is left as it is: it pauses projection grains only, so it keeps
      its own projection list (design D2, revised during apply). Verify:
      `tests/Stratara.Orleans.Tests/ReplayCheckpointResetTests.cs` passes unchanged.
      Done: untouched; 4/4 within the 82.
- [x] 1.3 `src/Stratara.Orleans/Stratara.Orleans.csproj` grants `InternalsVisibleTo` to
      `Stratara.Orleans.EntityFrameworkCore`. Verify: the csproj; the build.

## 2. Scope the reset

- [x] 2.1 `ExecutionModelReset` deletes only the checkpoints whose consumer is one of those names, and
      reports that count; with no names it deletes none. Verify: `src/Stratara.Orleans.EntityFrameworkCore/Hosting/ExecutionModelReset.cs`.
      Done: the union of the registered targets' names filters the delete; the registration's XML docs say so too.
- [x] 2.2 The reset integration test writes a checkpoint of a projection the host does not register
      beside the host's own, and asserts that it survives the reset with its position while the host's
      checkpoints are gone and counted. Verify:
      `tests/Stratara.Orleans.IntegrationTests/Hosting/ResetTests.cs`, run against PostgreSQL.
      Done: two checkpoints of the host's consumer and one of another; the report counts 2 and the other keeps 42.
      Against the unfiltered delete the test fails (3 removed); with the change it passes.
- [x] 2.3 The test's existing probe checkpoint uses a consumer name the host registers, or the
      assertion on the removed count is adjusted to what the host registers. Verify: the same test.
      Done: the host registers a grainless store reader named `probe`, so the probe checkpoints are its own.
- [x] 2.4 The XML documentation of `IExecutionModelReset` and `ExecutionModelResetReport.Checkpoints`
      says whose checkpoints are removed. Verify: `src/Stratara.Orleans/Hosting/IExecutionModelReset.cs`.
      Done: summary, remarks (resolve from the host's own composition) and the report parameter.

## 3. Documentation

- [x] 3.1 `docs/guides/operate-the-orleans-execution-model.md` → *Reset what the model keeps* says the
      reset removes the checkpoints of the projections and sagas the host registers, must run from the
      host's own composition, and leaves a removed projection's checkpoints in place, inert.
      Verify: a read of that section; documentation tests.
      Done: the section says whose checkpoints, where to resolve the reset, and what stays; the concept page's
      *A clean slate on demand* says the same.
- [x] 3.2 Where the documentation configures the read store for the execution model
      (`AddStrataraProjectionCheckpoints`), it states the sharing limit: distinct consumer names, and at
      most one deployment running store-reading sagas per read store. Verify:
      `grep -rn "AddStrataraProjectionCheckpoints" docs` and a read of each hit's section.
      Done: new subsection *Sharing a read store* in the operations guide; the migration guide's schema table links
      it; the cheatsheet rows of `AddStrataraProjectionCheckpoints` and `AddStrataraExecutionModelReset` say it.
- [x] 3.3 `CHANGELOG.md` `[Unreleased]` → *Changed* names the narrower reset and the sharing limit.
      Verify: the entry.
      Done: two *Changed* entries.

## 4. Close

- [x] 4.1 `./scripts/local-gauntlet.sh` green; the `Hosting` and `Projections` integration namespaces
      green against PostgreSQL. Verify: the run output, recorded here.
      Done 2026-09-16: gauntlet green (documentation tests included); `Hosting` 7/7, `Projections` 8/8.
- [x] 4.2 `openspec validate scope-the-reset-to-the-hosts-consumers --strict` passes. Verify: the output.
      Done: "Change 'scope-the-reset-to-the-hosts-consumers' is valid".
