## 1. Decision

- [x] 1.1 **Owner decision, 2026-09-03: a bounded retry, nothing larger.** Recorded in `design.md`
      with the reasoning against the three shapes finding F-008 proposed. Nothing to build; the box
      is ticked when the owner confirms the record is accurate.

## 2. The sixth policy

- [x] 2.1 `src/Stratara.Resilience/Resilience/ResilienceNames.cs`: add `ProjectionReplayBatch`,
      documented like its five siblings — five attempts in all, exponential from one second with
      jitter, any exception, cancellation excluded.
- [x] 2.2 `ResilienceFactory.CreateProjectionReplayBatchPipeline` with named constants for the
      attempt count and base delay; `ResilienceServiceCollectionExtensions.AddResiliencePipelines`
      registers it. Update the extension's XML doc, which lists the policies.
- [x] 2.3 `tests/Stratara.Shared.Tests/Resilience/ResilienceFactoryTests.cs`: the policy retries an
      arbitrary exception and returns the eventual result; it stops after its bound and surfaces the
      last exception unchanged; it does not retry `OperationCanceledException`.
- [x] 2.4 `tests/Stratara.Shared.Tests/DependencyInjection/ResilienceServiceCollectionExtensionsTests.cs`:
      the sixth name resolves, and registering twice still yields one policy per name.

## 3. The replay retries a batch

- [x] 3.1 `ProjectionReplayWorker` takes `ResiliencePipelineProvider<string>` and resolves
      `ResilienceNames.ProjectionReplayBatch` once, as the projection worker does for its policies.
- [x] 3.2 Extract one batch iteration — create scope, read entries after `afterSequence`, apply each
      under its recorded session, return the last sequence number and count — into a method the
      policy executes. A failed attempt disposes its scope; the next attempt starts from the same
      `afterSequence` in a fresh one. Progress is reported and `ProjectionReplayBatchPublished`
      logged only after the batch succeeds, as today.
- [x] 3.3 `src/Stratara.Diagnostics/LogEvents.cs`: `Projection.ProjectionReplayBatchFailed = 104_011`.
      `src/Stratara.Projections/Diagnostics/Extensions/LoggerProjectionExtensions.cs`:
      `LogProjectionReplayBatchFailed(exception, afterSequence, attempt)` at Warning, source-generated.
      Emitted from the executed delegate with an attempt counter, the pattern the projection worker
      uses for the preceding-fact retry — the policy is built in the factory without a logger, so
      `OnRetry` has nothing to log through. Cancellation is not logged.
- [x] 3.4 The `ExecuteAsync` outer catch, `SetFailed`, the truncation and the deactivate `finally` are
      untouched; confirm by reading the diff, not by assumption.

## 4. Tests on the replay path

`tests/Stratara.Projections.Tests/Services/ProjectionReplayWorkerTests.cs` — the harness gains a
mocked pipeline provider, as the other worker harnesses have, returning an empty pipeline by
default and a zero-delay five-attempt retry for the retry tests.

- [x] 4.1 *A batch fails once and then succeeds*: the projection manager throws on the first call and
      succeeds afterwards; the replay completes, the failing batch's entries were each handed to the
      manager at least twice, `SetFailed` was never called, and the retry warning was logged once.
- [x] 4.2 *Reading a batch fails once and then succeeds*: the repository throws on the first read after
      a given sequence and answers on the second; the replay completes and `afterSequence` advanced
      exactly once for that batch.
- [x] 4.3 *A batch fails on every attempt*: the manager always throws; the replay ends with
      `SetFailed` carrying the last exception's message, `Deactivate` called, and the manager invoked
      as many times as the policy allows. The harness's zero-delay pipeline keeps the test from
      waiting on real backoff; the real policy's shape is pinned in `ResilienceFactoryTests`.
- [x] 4.4 *The host shuts down while a batch is being retried*: cancellation during a retry ends the
      replay with no `SetFailed`, as `ReplayCallback_OperationCanceledException_IsSwallowedNoSetFailed`
      pins for the unretried case.
- [x] 4.5 The existing eight tests pass unchanged apart from the harness.

## 5. Documentation and changelog

- [x] 5.1 `docs/guides/write-a-projection.md`, *Replay is destructive, and it is all-or-nothing*:
      state that a batch is retried a bounded number of times, with backoff, before the
      replay fails, that a retried batch is re-applied from its start, and — under the existing
      "treat a failed replay as run it again" — that a replay is a maintenance operation to run in a
      window after a backup, the backup being the fallback when a replay cannot complete.
- [x] 5.2 `CHANGELOG.md` `[Unreleased]`: the retry, the new policy name, the new event id, and a
      pointer at the converge-not-accumulate contract a retry relies on.
- [x] 5.3 `./scripts/local-gauntlet.sh` green; `openspec validate --strict` clean.
