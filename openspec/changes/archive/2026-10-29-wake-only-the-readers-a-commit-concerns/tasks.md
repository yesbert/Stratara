## 1. Red first

- [x] 1.1 *A commit of an event one projection handles*: a host on the PostgreSQL store with the native reader,
  three projections and one saga. One projection handles `TagCreated`, and the poll interval is far longer than the
  test's wait. Commit ten `TagCreated` facts and assert that the handling projection applied them. Count the
  `projection_checkpoint` writes (`pg_stat_user_tables.n_tup_upd` before and after) and the store reads per reader
  (`OrleansReaderApplied` and a read counter on a decorating `ICommittedPositionReader`), and assert that only the
  handling projection read and wrote. Run it against the unchanged code and record that it fails
  (`tests/Stratara.Orleans.IntegrationTests/Projections/CommitWakesItsReadersTests.cs`). Result on the unchanged
  code: 59 reads for 10 commits (bound 22), and the bystanders' checkpoints written
- [x] 1.2 *A commit of an event no consumer handles*: same host, a fact of a type nobody handles. No checkpoint is
  written and no reader reads for it (`CommitWakesItsReadersTests.cs`). Result on the unchanged code: 60 reads
- [x] 1.3 *The readers that applied nothing write their checkpoint later*: a reader that handles nothing of a commit
  is caught up by a forced catch-up (`CatchUpAsync`). Its stored checkpoint is unchanged until
  `IdleCheckpointInterval` (shortened) passes and is then past the entries. A projection wait covering those entries
  answers *reached* before the write (`CommitWakesItsReadersTests.cs`). Result on the unchanged code: the
  checkpoint was written by the catch-up itself
- [x] 1.4 *An idle silo*: a host with 20 projections × 8 partitions, poll interval 500 ms, nothing committed for five
  intervals. Assert that the decorating reader saw no `ReadAfterAsync` and at most `partitions × 2 × intervals`
  `HeadAsync` calls. Native reader and portable reader. Record the counts on the unchanged code
  (`tests/Stratara.Orleans.IntegrationTests/Projections/IdlePollTests.cs`). The test host has 6 projections × 16
  partitions. Result on the unchanged code: 952 and 954 reads in five seconds

## 2. Relevance per consumer (design D1)

- [x] 2.1 Read `EventMapperFactory` and record how upcasters are keyed (by stored type name, or by payload). Write
  down the "undecidable" test the nudge uses, so it asks what the mapper would
  (`src/Stratara.Shared/EventSourcing/Mapping/EventMapperFactory.cs`, note in this file). Result: the mapper decides
  `Reads(name, payload, relevance)` after `IEventUpcasterPipeline.Upcast`. The framework's `EventUpcasterPipeline`
  keys upcasters by the source type's version-independent name, and a pipeline of the host's own may read the
  payload. A wake-up carries no payload, so the name is upcast through the framework's pipeline with an empty payload
  and the name it ends at is resolved; it is undecidable, and wakes every reader, when that fails, when the pipeline
  is not the framework's, or when no resolver is registered (design D1, revised after the code review)
- [x] 2.2 `ProjectionRegistrations` keeps, per projection name, the relevant event type names it already builds the
  projections for. `SagaRegistrations` does the same, with "always" for a saga whose relevance `IsAnyResolvable`
  (`src/Stratara.Orleans/Projections/ProjectionRegistrations.cs`, `src/Stratara.Orleans/Sagas/SagaRegistrations.cs`)
- [x] 2.3 A `CommitRelevance` (internal) that decides, for a set of committed type names, which consumers of a target
  to wake. A name in the consumer's set, an "always" consumer or an undecidable name wakes. Unit tests: exact match,
  no match, "always", unresolvable name, upcast name (`tests/Stratara.Orleans.Tests/CommitRelevanceTests.cs`)
- [x] 2.4 `INudgeTarget.NudgeAsync(grainFactory, partition, eventTypeNames)`. `null` names wake every consumer, which
  is the earlier wake-up's meaning. `LocalNudges` and `OrleansEventBundleDispatcher` pass the bundle's
  `EventMessage.EventTypeName`s, and the restart and resume paths keep passing `null`
  (`src/Stratara.Orleans/Projections/ProjectionGrain.cs`, `StoreWakeUpGrain.cs`, `OrleansEventBundleDispatcher.cs`,
  `src/Stratara.Orleans/Sagas/SagaReaderGrain.cs`)
- [x] 2.5 Tests 1.1 and 1.2 pass. *A stateful saga process and a fact it did not declare*: a process reader is
  woken by a commit of a type it does not declare, and a stateless saga is not
  (`tests/Stratara.Orleans.Tests/CommitWakeUpTargetTests.cs`). The process decides as before, and
  `SagaProcessTimeoutTests` covers that

## 3. Cluster wake-up (design D2)

- [x] 3.1 `ClusterWakeUps.Wake(grainFactory, buckets, eventTypeNames)` sets `stratara.wake.event-types` in the
  `RequestContext` around the calls it issues and restores the previous value. `StoreWakeUpGrain.WakeAsync` reads it
  and passes the names to `LocalNudges.NudgeAsync`, or `null` without it (`src/Stratara.Orleans/Projections/StoreWakeUpGrain.cs`, design D2)
- [x] 3.2 Unit test: the key is set during the call and gone afterwards, and a value already set by the caller
  survives (`tests/Stratara.Orleans.Tests/StoreWakeUpTests.cs`)
- [x] 3.3 *A client host wakes the readers through the cluster*: the silo's wake-up passes the carried types to the
  real projection target, which wakes only the projection that applies them
  (`tests/Stratara.Orleans.Tests/CommitWakeUpTargetTests.cs`). `ClientWakeUpTests` shows that the cluster path still
  wakes before the poll. A per-reader read count in that cluster is not attributable to the commit, because the
  class's five-second keep-alive ticks poll too
- [x] 3.4 *A host of the earlier version commits during a rolling deployment*: a wake-up without types wakes every
  reader of the partitions (`CommitWakeUpTargetTests.A_wake_up_through_the_cluster_wakes_the_projections_its_types_concern_and_every_one_without_types`)

## 4. Deferred checkpoint (design D3)

- [x] 4.1 `IdleCheckpointInterval` on `ProjectionGrainOptions`, default 30 s, with XML documentation.
  `TimeSpan.Zero` means write at once, and the validator refuses a negative value. Saga readers pass
  `TimeSpan.Zero` (design D7) (`src/Stratara.Orleans/Projections/ProjectionGrain.cs`,
  `src/Stratara.Orleans/Hosting/OrleansOptionsValidator.cs`, `tests/Stratara.Orleans.Tests` validator tests)
- [x] 4.2 `BatchOutcome(Processed, Effective)` from the batch delegate. `ApplyEachAsync` counts an entry as effective
  when it handed anything to a handler. The projection and saga grains report it
  (`src/Stratara.Orleans/Projections/StoreReaderLoop.cs`, `ProjectionGrain.cs`, `SagaReaderGrain.cs`)
- [x] 4.3 `_written` beside `_position`. Effective whole batches write at once, non-effective ones defer, and cut
  batches write at once. The deferral is flushed at the end of a catch-up and on a poll tick once it is older than
  the interval, and on deactivation (`StoreReaderGrain.OnDeactivateAsync`). `Invalidate` drops it. Every
  `AdvanceAsync` goes from `_written` (`StoreReaderLoop.cs`, `StoreReaderGrain.cs`). Unit tests in
  `tests/Stratara.Orleans.Tests/StoreReaderLoopTests.cs` cover a hold-back and its flush, a batch that applied
  something, an invalidated hold-back, a write after the interval and none while suspended, and a cut batch
- [x] 4.4 Test 1.3 passes. *A rebuild pauses a reader that holds a position not yet written*: a reader with a deferral
  is paused for a rebuild. Its checkpoint is 0 when the read model is emptied, and the read model holds every fact
  afterwards (`tests/Stratara.Orleans.IntegrationTests/Projections/RebuildEndToEndTests.cs`)
- [x] 4.5 *A silo dies before a deferred checkpoint is written*: kill the silo with a deferral pending, start a second
  one, and assert the entries are re-read with nothing applied and the later facts applied once. Model it on
  `CommitPublishKillTests` (`tests/Stratara.Orleans.IntegrationTests/Projections/DeferredCheckpointKillTests.cs`)
- [x] 4.6 *A saga reader reads a batch that applies nothing*: a saga reader caught up over facts its saga does not
  handle has its checkpoint past them at once, and a saga added afterwards reacts to none of them
  (`tests/Stratara.Orleans.IntegrationTests/Sagas/`, next to the test of *A saga is added to a running deployment*)
- [x] 4.7 The existing checkpoint tests stay green: `CheckpointFirstWriteTests`, *A stale activation writes a
  checkpoint*, `SeededStartTests`, `ProjectionProgressTests`, `ReplayWithStoreReadersTests`

## 5. Idle poll (design D4)

- [x] 5.1 `PartitionHeads`, a silo singleton: a per-partition cached head with its time, refreshed single-flight
  through `HeadAsync` once older than half the shortest poll interval of the silo's projection and saga options.
  It is disabled when the registered `ICommittedPositionReader` does not override `HeadAsync` (interface map). Unit
  tests: refresh once under concurrent callers, staleness bound, disabled for a default-head reader
  (`src/Stratara.Orleans/Projections/PartitionHeads.cs`, `tests/Stratara.Orleans.Tests/PartitionHeadsTests.cs`)
- [x] 5.2 `StoreReaderGrain.RequestPoll` asks `PartitionHeads` before it starts a catch-up. Nudges, `CatchUpAsync`,
  restarts and waits bypass it. A stalled reader still retries once per poll (`StoreReaderGrain.cs`;
  `BusyReaderKeepAliveTests` and the stall tests stay green)
- [x] 5.3 Test 1.4 passes on both readers. *A wake-up is lost*: with nudges suppressed, a committed fact is applied
  within 1.5 poll intervals plus tolerance (`IdlePollTests.cs`)

## 6. Documentation and release notes

- [x] 6.1 `docs/guides/operate-the-orleans-execution-model.md`: which readers a commit wakes (and when everyone is
  woken: stateful saga processes, undecidable names, wake-ups from earlier versions), when a checkpoint is written,
  what a restart re-reads, what a lost wake-up costs, `IdleCheckpointInterval`, and the risk for a saga added to a
  running deployment, which is why saga readers write at once (design D7)
- [x] 6.2 XML docs of `PollInterval` on both options say the poll reads only when the partition has moved
- [x] 6.3 `CHANGELOG.md` `[Unreleased]`: Changed (wake-up and poll cost), Added (`IdleCheckpointInterval`), naming the
  measured before/after from 1.1 and 1.4
- [x] 6.5 `ExecutionModelTestHost.WaitForReadersAsync` asks each projection and saga reader for its position
  instead of reading the stored checkpoint (`src/Stratara.Testing.Orleans/ExecutionModelTestHost.cs`). Found by
  `OrleansExecutionModelSampleSmokeTests` in the gauntlet. *A projection's reader holds its checkpoint back*:
  `ExecutionModelTestHostTests.The_wait_for_the_readers_returns_while_a_reader_holds_its_checkpoint_back` times out
  against the old wait and passes against the new one
- [x] 6.4 `openspec validate wake-only-the-readers-a-commit-concerns --strict`, `./scripts/local-gauntlet.sh`, and the
  integration classes touched here (`CommitWakesItsReadersTests`, `IdlePollTests`, `DeferredCheckpointKillTests`,
  `RebuildEndToEndTests`, the store wake-ups and saga tests of 2.5 and 3.3)
  Result: gauntlet green. The integration namespaces `Projections`, `Sagas` and `Hosting` ran twice in full on a
  machine under heavy unrelated load (load average 20–39). Each run had a handful of timeouts in tests whose
  latency bounds the load broke (`ClientWakeUpTests`, `CommitPublishKillTests`, `StoppingSiloTests`, one
  `SagaIsolationTests` case), and every one passed when rerun alone or by class. The new classes pass
