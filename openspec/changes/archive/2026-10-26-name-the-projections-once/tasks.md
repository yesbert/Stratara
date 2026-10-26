## 1. Red first

- [x] 1.1 Unit test: resolving `OrleansEventBundleDispatcher` in a fresh scope, with two projections registered
  through factories that count their constructions, constructs none of them once the registry has been asked. Run
  it against the current code and record that it fails
  (`tests/Stratara.Orleans.Tests/ProjectionRegistrationsTests.cs`). Result: it failed. Resolving the dispatcher
  constructed the projection once more (expected 1, actual 2)
- [x] 1.2 Unit test: `ProjectionRegistrations.Names` keeps registration order and drops a second projection that
  carries the same name. `NameOf` answers a registered type's name and null for any other type
  (`ProjectionRegistrationsTests.cs`)

## 2. The single source

- [x] 2.1 `ProjectionRegistrations` gains `Names` (ordered, distinct, ordinal) and `NameOf(Type)`, built in the same
  locked, uncached-on-failure computation as `Contains` (design D1)
  (`src/Stratara.Orleans/Projections/ProjectionRegistrations.cs`)
- [x] 2.2 `ProjectionNudgeTarget` takes `ProjectionRegistrations` in place of `IProjectionHandler` and
  `IEnumerable<IProjection>`, and returns `Names` as `ConsumerNames` (design D2)
  (`src/Stratara.Orleans/Projections/ProjectionGrain.cs`)
- [x] 2.3 `ProjectionProgress` drops `ProjectionNames` and its `_names` field and asks the registry. The refusal
  messages stay unchanged (design D3) (`src/Stratara.Orleans/Projections/ProjectionProgress.cs`)
- [x] 2.4 `ReplayCheckpointResetTruncator.TruncateAllAsync` reads `Names` from the registry (design D3)
  (`src/Stratara.Orleans/Projections/ReplayCheckpointReset.cs`)
- [x] 2.5 `ProjectionRebuilder.RebuildAsync` refuses a name the registry does not contain before it pauses any
  reader, and still resolves the instance it rebuilds (design D3)
  (`src/Stratara.Orleans/Projections/ProjectionRebuilder.cs`)
- [x] 2.6 Every unit test that built these types by hand passes the registry where it passed the handler and the
  projections. Adjust only their set-up, not their assertions (`tests/Stratara.Orleans.Tests`): `LostNudgeTests`,
  `ProjectionProgressTests`, `ProjectionRebuilderTests`, `ReplayCheckpointResetTests`

## 3. Green

- [x] 3.1 1.1 and 1.2 pass, and `tests/Stratara.Orleans.Tests` and `tests/Stratara.Testing.Orleans.Tests` are green
- [x] 3.2 The integration classes that use the touched paths stay green: `Projections/ProjectionProgressTests`,
  `Projections/RebuildEndToEndTests`, `Projections/OverlappingRebuildTests`, `Projections/ReplayWithStoreReadersTests`,
  `Projections/SeededStartTests`, `Projections/UnregisteredProjectionReaderTests`, `Projections/ProjectionGrainTests`,
  and `Hosting/ResetTests`, which reads the consumer names

## 4. Release notes

- [x] 4.1 `CHANGELOG.md` under `[Unreleased]`, *Changed*: on the Orleans execution model a commit no longer
  constructs every registered projection to learn the readers it wakes. The names are worked out once per silo

## 4a. Review findings (`/code-review high`; Copilot's quota was exhausted)

- [x] 4a.1 `ProjectionNudgeTarget` reads its names when it is built, as it did before, not on every use. A silo that
  cannot work them out fails where the target is resolved again, not inside a nudge's `catch`. The wake-up grain's
  activation fails as before, `TargetsOf`'s "fixed when they are built" holds, and the double forwarding is gone
  (`ProjectionGrain.cs`)
- [x] 4a.2 The commit-path test registers two projections, measures without a warm-up, resolves the dispatcher in
  three scopes, nudges through every target, and asserts two constructions and six wake-ups
  (`ProjectionRegistrationsTests.Commits_construct_each_projection_once_per_silo_to_learn_the_readers_they_wake`)
- [x] 4a.3 Answered without a change: a failure built again under the lock, the synchronous disposal of the
  registry's scope, the refusal of a non-rebuildable projection after the pause, and the rebuilder's fallback
  message. See the PR comment

## 5. Verification

- [x] 5.1 `openspec validate name-the-projections-once --strict`
- [x] 5.2 `./scripts/local-gauntlet.sh` green
