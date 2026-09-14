## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it. *Record:* the owner approved in the session that proposed —
      asked "Freigeben und umsetzen?" on 2026-09-14 and chose it — so the artifacts were committed
      once, already approved; there is no separate proposed-to-approved commit.

## 1. Pre-register before any run

- [x] 1.1 `evidence/expectations.md` states, for every re-run measurement (B2, B3, B4, B5) and the new
      restart-delay run (R1), the archived baseline, the expectation, the falsification bound, the
      profile and directory setting, and the expected duration. Verify: committed before any file under
      `evidence/raw/` exists.
- [x] 1.2 The owner confirms the thresholds. Verify: the confirmation date at the top of
      `evidence/expectations.md`, in the same or a later commit, before the first raw result.

## 2. Rebuild (D1, D2)

- [x] 2.1 `ProjectionRebuilder.RebuildAsync` resumes every partition with one `Task.WhenAll`;
      `ProjectionGrain.ResumeAsync` clears the pause and requests a catch-up without awaiting it.
      Verify: `tests/Stratara.Orleans.Tests` unit test on the rebuilder's call pattern, and
      `ProjectionGrainTests` green.
- [x] 2.2 `StoreReaderLoop.CatchUpAsync` takes a batch applier returning the first unapplied index;
      `ProjectionGrain` and `SagaGrain` apply a batch under one scope with the projection, handler and
      relevant-set resolved once. Verify: `ProjectionGrainTests` (idempotent apply, genuine failure
      stops the checkpoint, missing prerequisite retried without advancing) and `SagaGrainTests` green.
- [x] 2.3 Run B4 under the production profile: `dotnet run --project tests/Stratara.Orleans.Benchmarks
      -c Release -- --rebuild <evidence-dir>`. Verify: `evidence/raw/rebuild/<timestamp>/` exists and
      `results.md` compares the ratio with the archived 94 %.
- [x] 2.4 *Not needed:* 2.3 measured 17.6 % against the 40 % expectation. Only if 2.3 misses its expectation: a stored partition column with an index on
      (partition, transaction id) in `CommitOrderModel`, the reader filtering on it, and B4 re-run.
      Verify: a second `evidence/raw/rebuild/` directory and the delta in `results.md`.

## 3. Hint path (D3, D4)

- [x] 3.1 `NudgeAsync` is `[OneWay]` and `[AlwaysInterleave]` on the projection and saga grain
      interfaces; the grains run one single-flight loop that drains while dirty; poll timer, reminder
      and explicit `CatchUpAsync` go through it; `PauseAsync` awaits a running loop. Verify:
      `ProjectionGrainTests`, `SagaGrainTests`, `CommitPublishKillTests` (checkpoint path) green.
- [x] 3.2 `StoreReaderLoop` keeps its position between catch-ups and re-reads the store after a pause;
      `ProjectionCheckpointStore.SetAsync` is one upsert. `ProjectionGrainTests` resets checkpoints
      through pause and resume. Verify: the tests above green and `ResetTests` green.
- [x] 3.3 Run B2 under the production profile: `-- --read-model-latency <evidence-dir>`. Verify:
      `evidence/raw/read-model-latency/<timestamp>/` and the comparison with the archived p50/p99.

## 4. Command path (D5, D6, D7, D8)

- [x] 4.1 `AggregateCommandEnvelope` is `[Immutable]`. Verify: `tests/Stratara.Orleans.Tests` builds and
      `ArrivalOrderTests` green.
- [x] 4.2 A per-silo intent completion queue flushes completed intent ids in one delete per bounded
      window, and on host stop; `CommandExecution` enqueues instead of deleting; the window is an
      option on `OrleansDispatchOptions` documented against `IntentGrace`. Verify: a unit test on the
      queue's flush bounds in `tests/Stratara.Orleans.Tests`, and `DurableIntentTests` green (kill
      between hand-off and completion still resumes).
- [x] 4.3 `PocSilo.Configure` takes a profile (`Test`, `Production`) and a directory setting (Redis as
      default, or Redis named for the long-lived grains with the built-in directory for aggregate and
      runner grains); the long-lived grains carry `[GrainDirectory]`. Tests keep `Test` and Redis as
      default. Verify: the suite green under the test default (`raw/integration-suite/20260914-104800/`),
      and B3 and B5 complete with 0 conflicts and 12 000 of 12 000 applied under both settings
      (`raw/commands-per-aggregate/`, `raw/resources/`). No test class runs under the built-in setting.
- [x] 4.4 Run B3 under the production profile, both directory settings: `-- --commands-per-aggregate
      <evidence-dir>`. Verify: `evidence/raw/commands-per-aggregate/<timestamp>/` and the comparison
      with the archived 131 / 150 / 87 % (intent) and 166 / 223 / 99 % (synchronous).
- [x] 4.5 Run B5 under the production profile, both directory settings: `-- --resources <evidence-dir>`.
      Verify: `evidence/raw/resources/<timestamp>/` and the comparison with the archived 4.04 against
      2.54 CPU-s per 1 000 commands and 231 against 172 MB idle.

## 5. Restart delay (D9)

- [x] 5.1 A `--restart-delay <evidence-dir>` run: start the intent host, kill it, restart on the same
      endpoint, measure restart to first successful grain call; default membership options and the
      shortened profile, three restarts each. Verify: `evidence/raw/restart-delay/<timestamp>/`.
- [x] 5.2 `HardKillTimerTests` runs once under the shortened profile. Verify: 10 of 10 kills as expected,
      no false death vote in the silo log, recorded in `results.md`.

## 6. Close

- [x] 6.0 `evidence/operations-note.md` states the hard-death finding with its three answers, the
      directory per grain type, the profile, and the bus path's cost since 4.0.4. Verify: the file
      cites the raw directories for each claim.

- [x] 6.1 Every integration test of the archived change is green on the final code:
      `dotnet test tests/Stratara.Orleans.IntegrationTests`. Verify: the run's summary in `results.md`.
- [x] 6.2 `evidence/results.md` states every number against its archived baseline and its expectation,
      names the profile and directory setting per number, records falsified expectations as such, and
      ends with what the numbers say about the two costs that decided the recommendation. Verify: every
      number cites its raw directory.
- [x] 6.3 `./scripts/local-gauntlet.sh` passes and the diff touches nothing under `src/` outside
      `src/Stratara.Orleans/`, no packable project, and no spec. Verify: `git diff --stat main`.
