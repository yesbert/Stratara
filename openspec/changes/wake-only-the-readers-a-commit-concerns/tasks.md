## 1. Red first

- [ ] 1.1 *A commit of an event one projection handles*: a host on the PostgreSQL store with the native reader,
  three projections and one saga. One projection handles `TagCreated`, and the poll interval is far longer than the
  test's wait. Commit ten `TagCreated` facts and assert that the handling projection applied them. Count the
  `projection_checkpoint` writes (`pg_stat_user_tables.n_tup_upd` before and after) and the store reads per reader
  (`OrleansReaderApplied` and a read counter on a decorating `ICommittedPositionReader`), and assert that only the
  handling projection read and wrote. Run it against the unchanged code and record that it fails
  (`tests/Stratara.Orleans.IntegrationTests/Projections/CommitWakesItsReadersTests.cs`)
- [ ] 1.2 *A commit of an event no consumer handles*: same host, a fact of a type nobody handles. No checkpoint is
  written and no reader reads for it (`CommitWakesItsReadersTests.cs`)
- [ ] 1.3 *The readers that applied nothing write their checkpoint later*: a reader that handles nothing of a commit
  is caught up by a forced catch-up (`CatchUpAsync`). Its stored checkpoint is unchanged until
  `IdleCheckpointInterval` (shortened) passes and is then past the entries. A projection wait covering those entries
  answers *reached* before the write (`CommitWakesItsReadersTests.cs`)
- [ ] 1.4 *An idle silo*: a host with 20 projections × 8 partitions, poll interval 500 ms, nothing committed for five
  intervals. Assert that the decorating reader saw no `ReadAfterAsync` and at most `partitions × 2 × intervals`
  `HeadAsync` calls. Native reader and portable reader. Record the counts on the unchanged code
  (`tests/Stratara.Orleans.IntegrationTests/Projections/IdlePollTests.cs`)

## 2. Relevance per consumer (design D1)

- [ ] 2.1 Read `EventMapperFactory` and record how upcasters are keyed (by stored type name, or by payload). Write
  down the "undecidable" test the nudge uses, so it asks what the mapper would
  (`src/Stratara.Shared/EventSourcing/Mapping/EventMapperFactory.cs`, note in this file)
- [ ] 2.2 `ProjectionRegistrations` keeps, per projection name, the relevant event type names it already builds the
  projections for. `SagaRegistrations` does the same, with "always" for a saga whose relevance `IsAnyResolvable`
  (`src/Stratara.Orleans/Projections/ProjectionRegistrations.cs`, `src/Stratara.Orleans/Sagas/SagaRegistrations.cs`)
- [ ] 2.3 A `CommitRelevance` (internal) that decides, for a set of committed type names, which consumers of a target
  to wake. A name in the consumer's set, an "always" consumer or an undecidable name wakes. Unit tests: exact match,
  no match, "always", unresolvable name, upcast name (`tests/Stratara.Orleans.Tests/CommitRelevanceTests.cs`)
- [ ] 2.4 `INudgeTarget.NudgeAsync(grainFactory, partition, eventTypeNames)`. `null` names wake every consumer, which
  is the earlier wake-up's meaning. `LocalNudges` and `OrleansEventBundleDispatcher` pass the bundle's
  `EventMessage.EventTypeName`s, and the restart and resume paths keep passing `null`
  (`src/Stratara.Orleans/Projections/ProjectionGrain.cs`, `StoreWakeUpGrain.cs`, `OrleansEventBundleDispatcher.cs`,
  `src/Stratara.Orleans/Sagas/SagaReaderGrain.cs`)
- [ ] 2.5 Tests 1.1 and 1.2 pass. Add *A stateful saga process and a fact it did not declare* to
  `tests/Stratara.Orleans.IntegrationTests/Sagas/` (the process reader is woken and decides as before)

## 3. Cluster wake-up (design D2)

- [ ] 3.1 `WakeForAsync(int[] buckets, string[] eventTypeNames)` on `IProjectionWakeUpGrain` and `ISagaWakeUpGrain`,
  `[AlwaysInterleave]`, alias `WakeForAsync`. `WakeAsync(int[])` stays and wakes every reader
  (`src/Stratara.Orleans/Projections/StoreWakeUpGrain.cs`)
- [ ] 3.2 Find out which exception Orleans 10.4 raises when the target silo's interface lacks the method: a test grain
  interface with a method the implementation's silo does not register. Record the type here. `ClusterWakeUps` falls
  back to `WakeAsync` on exactly that exception and remembers the old form for `AbsentRoleRetry`, under its own
  generation (`StoreWakeUpGrain.cs`, `tests/Stratara.Orleans.Tests/ClusterWakeUpsTests.cs`)
- [ ] 3.3 *A client host wakes the readers through the cluster*: extend the existing client-host wake-up test so that
  only the handling projection reads (`tests/Stratara.Orleans.IntegrationTests` — the store wake-ups test that
  covers *A command silo without a store-reading role registers the store wake-ups*)
- [ ] 3.4 *A host of the earlier version commits during a rolling deployment*: a call to `WakeAsync(int[])` wakes every
  reader of the partitions (`ClusterWakeUpsTests.cs` or the integration test of 3.3)

## 4. Deferred checkpoint (design D3)

- [ ] 4.1 `IdleCheckpointInterval` on `ProjectionGrainOptions` and `SagaGrainOptions`, default 30 s, with XML
  documentation. `TimeSpan.Zero` means write at once, and the validator refuses a negative value
  (`src/Stratara.Orleans/Projections/ProjectionGrain.cs`, `src/Stratara.Orleans/Sagas/SagaGrain.cs`,
  `src/Stratara.Orleans/Hosting/OrleansOptionsValidator.cs`, `tests/Stratara.Orleans.Tests` validator tests)
- [ ] 4.2 `BatchOutcome(Processed, Effective)` from the batch delegate. `ApplyEachAsync` counts an entry as effective
  when it handed anything to a handler. The projection and saga grains report it
  (`src/Stratara.Orleans/Projections/StoreReaderLoop.cs`, `ProjectionGrain.cs`, `SagaReaderGrain.cs`)
- [ ] 4.3 `_written` beside `_position`. Effective whole batches write at once, non-effective ones defer, and cut
  batches write at once. The deferral is flushed at the end of a catch-up and on a poll tick once it is older than
  the interval, and on deactivation (`StoreReaderGrain.OnDeactivateAsync`). `Invalidate` drops it. Every
  `AdvanceAsync` goes from `_written` (`StoreReaderLoop.cs`, `StoreReaderGrain.cs`)
- [ ] 4.4 Test 1.3 passes. *A rebuild pauses a reader that holds a position not yet written*: a reader with a deferral
  is paused for a rebuild. Its checkpoint is 0 when the read model is emptied, and the read model holds every fact
  afterwards (`tests/Stratara.Orleans.IntegrationTests/Projections/RebuildEndToEndTests.cs`)
- [ ] 4.5 *A silo dies before a deferred checkpoint is written*: kill the silo with a deferral pending, start a second
  one, and assert the entries are re-read with nothing applied and the later facts applied once. Model it on
  `CommitPublishKillTests` (`tests/Stratara.Orleans.IntegrationTests/Projections/DeferredCheckpointKillTests.cs`)
- [ ] 4.6 The existing checkpoint tests stay green: `CheckpointFirstWriteTests`, *A stale activation writes a
  checkpoint*, `SeededStartTests`, `ProjectionProgressTests`, `ReplayWithStoreReadersTests`

## 5. Idle poll (design D4)

- [ ] 5.1 `PartitionHeads`, a silo singleton: a per-partition cached head with its time, refreshed single-flight
  through `HeadAsync` once older than half the shortest poll interval of the silo's projection and saga options.
  It is disabled when the registered `ICommittedPositionReader` does not override `HeadAsync` (interface map). Unit
  tests: refresh once under concurrent callers, staleness bound, disabled for a default-head reader
  (`src/Stratara.Orleans/Projections/PartitionHeads.cs`, `tests/Stratara.Orleans.Tests/PartitionHeadsTests.cs`)
- [ ] 5.2 `StoreReaderGrain.RequestPoll` asks `PartitionHeads` before it starts a catch-up. Nudges, `CatchUpAsync`,
  restarts and waits bypass it. A stalled reader still retries once per poll (`StoreReaderGrain.cs`;
  `BusyReaderKeepAliveTests` and the stall tests stay green)
- [ ] 5.3 Test 1.4 passes on both readers. *A wake-up is lost*: with nudges suppressed, a committed fact is applied
  within 1.5 poll intervals plus tolerance (`IdlePollTests.cs`)

## 6. Documentation and release notes

- [ ] 6.1 `docs/guides/operate-the-orleans-execution-model.md`: which readers a commit wakes (and when everyone is
  woken: stateful saga processes, undecidable names, wake-ups from earlier versions), when a checkpoint is written,
  what a restart re-reads, what a lost wake-up costs, `IdleCheckpointInterval`, and the risk for a saga added to a
  running deployment (design, *Risks*)
- [ ] 6.2 XML docs of `PollInterval` on both options say the poll reads only when the partition has moved
- [ ] 6.3 `CHANGELOG.md` `[Unreleased]`: Changed (wake-up and poll cost), Added (`IdleCheckpointInterval`), naming the
  measured before/after from 1.1 and 1.4
- [ ] 6.4 `openspec validate wake-only-the-readers-a-commit-concerns --strict`, `./scripts/local-gauntlet.sh`, and the
  integration classes touched here (`CommitWakesItsReadersTests`, `IdlePollTests`, `DeferredCheckpointKillTests`,
  `RebuildEndToEndTests`, the store wake-ups and saga tests of 2.5 and 3.3)
