## 1. Red first

- [x] 1.1 *A host is started without a projection it ran before*: a host on the PostgreSQL store with the native
  reader and two projections, a shortened keep-alive and poll interval, as in `RetiredReaderTests`, runs and
  commits facts until both projections hold checkpoints and keep-alive reminders in every partition. It is stopped
  and started again with one projection removed, and the logs are captured. The test asserts that every partition
  of the removed projection logs `UnregisteredProjectionReaderRetired` once, that its reminder rows are gone, that
  no `CatchUpFaulted`, `PartitionStalled` or apply failure is logged for it, and that its checkpoints are unchanged.
  Run it against the unchanged code and record that it fails
  (`tests/Stratara.Orleans.IntegrationTests/Projections/UnregisteredProjectionReaderTests.cs`). Result: it failed.
  The first host passed, and on the second no reader of the removed projection retired within 60 s
- [x] 1.2 *Facts are committed after a projection's readers retired*: in the same host, facts committed in every
  partition after the retirement are applied by the remaining projection, nothing is logged for the removed one,
  and its checkpoints stay where they were (`UnregisteredProjectionReaderTests.cs`)
- [x] 1.3 *A projection is registered again*: a third start with both projections registered. The removed
  projection's readers resume from their checkpoints, apply only the facts committed while it was absent, and
  have their keep-alive reminders again (`UnregisteredProjectionReaderTests.cs`)
- [x] 1.4 *A saga is removed or renamed*: confirm that `Sagas/SagaIsolationTests` asserts the retirement, the log
  event `117_126` and the unregistered keep-alive. Add an assertion there that the saga's checkpoints are
  unchanged, if it does not have one yet. Result: the test seeds a checkpoint for the removed saga and asserts it is
  unchanged after the retirement

## 2. The silo's projection names

- [x] 2.1 `ProjectionRegistrations`, an internal singleton that builds its name set lazily on first use. It opens
  a scope, resolves `IProjectionHandler` and every `IProjection`, maps them through `GetProjectionName`, and
  answers `Contains(string consumer)`. It is registered once by `AddStrataraProjectionGrains`, next to
  `ProjectionNudgeTarget` (`src/Stratara.Orleans/Projections/ProjectionRegistrations.cs`,
  `src/Stratara.Orleans/DependencyInjection/OrleansProjectionServiceCollectionExtensions.cs`, design D2)
- [x] 2.2 Unit test: the handler's names are the ones used, not the type names; a factory-registered projection is
  found and a second question constructs nothing; a failure to work out the names is not kept
  (`tests/Stratara.Orleans.Tests/ProjectionRegistrationsTests.cs`)

## 3. Retirement

- [x] 3.1 `LogEvents.Orleans.UnregisteredProjectionReaderRetired = 117_136`, and
  `OrleansLog.LogUnregisteredProjectionReaderRetired(projection, partition)` at Information
  (`src/Stratara.Diagnostics/LogEvents.cs`, `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`, design D3)
- [x] 3.2 `ProjectionGrain` overrides `RetiresAs` to answer `Unregistered` where `ProjectionRegistrations` does
  not contain `Consumer`, and overrides `LogRetirement` to log 3.1. Update the class's XML summary
  (`src/Stratara.Orleans/Projections/ProjectionGrain.cs`, design D1 and D5)
- [x] 3.3 Check that `ResolveProjection`'s throw stays reachable only through a registration that changed after
  activation, and leave it as a guard. Its exception documentation stays accurate (`ProjectionGrain.cs`)

## 3a. A retirement is logged once (found during implementation, design D6)

- [x] 3a.1 The first green run of 1.1 logged every retirement three times, and the silo logged `Could not deliver
  reminder tick` once per partition: deactivating inside `OnActivateAsync` makes Orleans forward the activating
  tick. A retired reader that deactivates now does so in the first call it answers
  (`src/Stratara.Orleans/Projections/StoreReaderGrain.cs`, `AnswersAsRetired`)
- [x] 3a.2 `UnregisteredProjectionReaderTests` asserts exactly one retirement per partition and no undelivered tick.
  `RetiredReaderTests` and `SagaIsolationTests` assert one retirement and no undelivered tick as well. Both fail
  against the old base grain with three retirements

## 4. Green

- [x] 4.1 1.1 to 1.4 pass
- [x] 4.2 The touched integration classes stay green: `Projections/RetiredReaderTests`,
  `Projections/ProjectionGrainTests`, `Projections/ProjectionProgressTests`, `Projections/RebuildEndToEndTests`,
  `Projections/ReplayWithStoreReadersTests`, `Projections/BusyReaderKeepAliveTests`, `Sagas/SagaIsolationTests`

## 5. Documentation and release notes

- [x] 5.1 `docs/guides/operate-the-orleans-execution-model.md`, next to the saga reader's retirement: a projection
  reader brought back on a silo that registers no projection of its name unregisters its keep-alive, logs
  `117_136` (Information) naming the projection and the partition, reads nothing and deactivates. Its checkpoints
  stay until the host deletes the consumer's rows from `projection_checkpoint` once no silo registers it, and a
  reset does not remove them. This applies to a saga's checkpoints as well. Say what a rolling deployment that adds
  a projection or a saga does to the keep-alive (design, *Risks*)
- [x] 5.2 `docs/reference/log-events-schema.md`: add the `117_136` row
- [x] 5.3 Check `docs/concepts/orleans-execution-model.md`, the migration guide and `llms.txt` for a claim that
  this change contradicts. None of them describes reader retirement or the keep-alive
- [x] 5.4 `CHANGELOG.md`: an entry under `[Unreleased]`, *Fixed*. The CHANGELOG names no consumer findings, so
  F-029 is named in the proposal and the pull request instead

## 5a. Review findings (`/code-review high`; Copilot's quota was exhausted)

- [x] 5a.1 A reader retired as unregistered refuses a pause, so a rebuild or a replay that meets it fails naming it
  instead of believing the partition stopped (design D7) (`StoreReaderGrain.RefusePauseWhereUnregistered`,
  asserted in `UnregisteredProjectionReaderTests`)
- [x] 5a.2 Such a reader answers a wait as `StoreReaderState.Unregistered` after its budget, and `ProjectionProgress`
  counts it as behind instead of `MarkNotApplicable` (design D7) (`ProjectionProgressTests`,
  `UnregisteredProjectionReaderTests`)
- [x] 5a.3 The grain stores the retirement kind instead of a flag, and `Retired` is private (design D7)
- [x] 5a.4 `ProjectionRegistrations` builds its set under a lock instead of `LazyThreadSafetyMode.PublicationOnly`,
  so concurrent first activations construct the projections once
- [x] 5a.5 The guide, the log-event reference and the CHANGELOG no longer promise one retirement per reader: it is one
  per time a reader is brought back, which a rolling deployment repeats. The guide names the checkpoint column under
  either naming, and says what a rolling deployment does to a rebuild and a wait
- [x] 5a.6 Answered without a change: a projection whose construction throws fails every reader's activation (design,
  *Risks*); consolidating the four places that name projections is out of scope

## 6. Verification

- [x] 6.1 `openspec validate retire-a-reader-of-an-unregistered-projection --strict`
- [x] 6.2 `./scripts/local-gauntlet.sh` green
- [x] 6.3 The integration classes of 1.x and 4.2 green locally (Docker). The full suite runs through
  `integration.yml` after the merge
