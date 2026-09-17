## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. The session is in place before anything is resolved (D1, D2)

- [x] 1.1 A failing test on today's code: a probe projection depending on a scoped service that captures
      the ambient tenant in its constructor; two tenants' entries in one partition and one read; the
      captured tenant per entry differs from the entry's. Verify: a new test class in
      `tests/Stratara.Orleans.IntegrationTests/Projections` (scenario *One read returns entries of several
      tenants*); record that it fails against today's `ProjectionGrain`.
      *Recorded 2026-09-17.* `TenantPerEntryTests` holds the projection, the saga (1.3) and the process (1.4) case in one
      class, with the facts appended while a replay holds the readers back so one read returns all of them. Against
      the previous code all three fail: every captured tenant is `00000000-…`, the constructed dependency having been
      resolved before any session was set.
- [x] 1.2 `ProjectionGrain.ApplyBatchAsync` applies the batch in session runs, one scope per run, the session
      set before the handler and the projection are resolved; `StoreReaderLoop.ApplyEachAsync` keeps its
      contract with a run-boundary callback. Verify: `src/Stratara.Orleans/Projections/ProjectionGrain.cs`,
      `StoreReaderLoop.cs`; 1.1 green; `StoreReaderLoopTests` green.
      *Recorded 2026-09-17.* The run boundary lives in a `SessionRuns` helper the grain's entry callback enters, rather
      than in a second callback on `ApplyEachAsync`: the loop's contract and its single retry path are unchanged, and a
      run is keyed by the entry's raw session fields, so entries without a correlation still share a run.
- [x] 1.3 The same for `SagaGrain.ApplyBatchAsync` with the saga manager and the processes, and the same
      test with a probe saga. Verify: `src/Stratara.Orleans/Sagas/SagaGrain.cs`; a test in
      `tests/Stratara.Orleans.IntegrationTests/Sagas` (scenario *A saga's dependency takes its tenant at
      construction*).
- [x] 1.4 `ISagaProcessGrain.HandleAsync` overload carrying the recorded session in a serialisable carrier;
      the grain sets it before resolving the process and reading the fact; the old method kept for one
      release. Verify: `src/Stratara.Orleans/Sagas/SagaProcessGrain.cs`; a test with a resolver that
      throws for an absent tenant (scenario *A fact reaches a process through a tenant-routed store*).
      *Recorded 2026-09-17.* The process test observes the tenant through a decorated aggregation service that takes
      its tenant at construction, not a throwing resolver: the store readers resolve their contexts under no session
      by design, so a resolver that throws for an absent tenant would stop the readers before the process is reached.
- [x] 1.5 Documentation: the migration guide says a read is multi-tenant and each entry is applied under
      its own session; `write-a-saga.md` and the guide's process section say the timeout path reads the
      state stream before a session is in place. Verify: the sections; documentation tests.

## 2. The checkpoint is guarded (D3)

- [x] 2.1 A failing test on today's code: two `ProjectionCheckpointStore` instances over one database, the
      first advances 0→10, the second 10→20, the first writes 15 and the row says 15. Verify:
      `tests/Stratara.Orleans.Tests/ProjectionCheckpointStoreTests.cs`; record the failure.
- [x] 2.2 `IProjectionCheckpointStore.AdvanceAsync(projection, partition, reader, from, to, ct)` with a
      default that calls `SetAsync`, documented. Verify:
      `src/Stratara.Abstractions/Abstractions/Projections/IProjectionCheckpointStore.cs`; a unit test that
      the default reaches `SetAsync` on a fake.
- [x] 2.3 The shipped store: conditional update by position and reader; insert on an absent row at `from == 0`
      with the key-race fallback; refusal naming both positions, or both readers; `SetAsync` refuses another
      reader. Verify: `ProjectionCheckpointStore.cs`; 2.1 green; a test per refusal (scenarios *A stale
      activation writes a checkpoint*, *A checkpoint is written under another reader's name*);
      `SurfaceTests` updated.
      *Recorded 2026-09-17.* Against the previous store the stale write leaves the row at 15. `SurfaceTests` needed no
      change: the port is in `Stratara.Abstractions`, and the store's public surface is unchanged. The guard tests run
      on SQLite in memory, because the in-memory provider has no conditional update.
- [x] 2.4 The loop advances instead of setting, after a batch and after a cut batch, and invalidates its
      cached position when refused. Verify: `StoreReaderLoop.cs:212-227`; a `StoreReaderLoopTests` case
      where a refusal is followed by a catch-up that reads the store's position.

## 3. A reader beyond the partition count retires (D4)

- [x] 3.1 `StoreReaderSettings` carries the partition count; `StoreReaderGrain.OnActivateAsync` retires a grain
      at or beyond it — unregisters the keep-alive, logs the retirement, no poll, no loop, deactivates on idle;
      every method a no-op. Verify: `StoreReaderGrain.cs`, `ProjectionGrain.cs`, `SagaGrain.cs`,
      `LogEvents.cs`, `OrleansLog.cs`.
      *Recorded 2026-09-17.* The three events are Information, and the band keeps Information in `117_0xx` and
      warnings in `117_1xx`, so they took `117_005`–`117_007`; `117_114` went to the heavy-work change.
- [x] 3.2 A cluster at four partitions with a reader's reminder for partition 3, reset and restarted at two: the
      reminder tick retires the grain, the reminder is gone, no stall counted. Verify: a test in
      `tests/Stratara.Orleans.IntegrationTests/Projections` (scenario *The partition count is lowered under
      the native reader*).
      *Recorded 2026-09-17.* `RetiredReaderTests` restarts the cluster at two partitions without resetting the
      reminders — the situation retirement is for; no checkpoint is involved, since a retired reader reads none.

## 4. A held-back resumption is logged (D5)

- [x] 4.1 `ReplaySuspensionTracker` (singleton); the hold, release and retirement events in `LogEvents.Orleans` at the next free ids of the band (`117_114`–`117_116` as of #109, renumbered if taken) and in `OrleansLog`;
      `OutboxDrainWork` and `OrleansCommandDispatcher.ResumeDueAsync` report through it. Verify:
      `src/Stratara.Orleans/Singleton/OutboxDrainWork.cs:74-77`, `OrleansCommandDispatcher.cs:62`, the
      registrations.
- [x] 4.2 Three runs under an active replay log one hold, the first run after it one release, and nothing
      without a hold. Verify: `tests/Stratara.Orleans.Tests/RecordedCommandDrainTests.cs` (scenario *A
      resumption is held back by a replay*).

## 5. Documentation

- [x] 5.1 `docs/reference/log-events-schema.md` rows for the three events;
      `docs/guides/operate-the-orleans-execution-model.md` names the retired reader and the held-back
      resumption under what to watch; `CHANGELOG.md` `[Unreleased]` *Added* (`AdvanceAsync`, the events)
      and *Fixed* (the tenant of a batch, the checkpoint guard, the retired reader); `llms.txt`. Verify: the
      entries; doc-symbol check.

## 6. Close

- [x] 6.1 `./scripts/local-gauntlet.sh` green; `Projections`, `Sagas` and `Singleton` integration namespaces
      green against PostgreSQL. Verify: the run output, recorded here.
      *Recorded 2026-09-17.* Local gauntlet green. `Projections`, `Sagas`, `Singleton`, `Hosting`, `Aggregates` and
      `HeavyWork` integration namespaces 53 of 53, after two fixes the first run found: the shipped store's `SetAsync`
      refused a row a concurrent writer had just inserted under the same reader (two overlapping rebuilds, caught by
      `OverlappingRebuildTests`; a concurrency test now covers it), and the intent scenario host waited out the
      thirty-second permit grace the heavy-work change introduced, which the heavy `DurableIntentTests` could not
      absorb — its test profile now shortens `PermitLease`, as the heavy scenario does.
- [x] 6.2 `openspec validate keep-a-store-reader-batch-tenant-correct-and-its-checkpoint-guarded --strict`
      passes. Verify: the output.
