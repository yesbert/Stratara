# Name the projections once

> **Status:** approved

## Why

On the Orleans execution model, five places each work out which projections the silo registers. They resolve every
projection and ask the projection handler for its name:

- the store readers' retirement;
- the nudge target, which starts the readers at silo start and wakes them after every commit;
- the wait for a projection, by name and by type;
- the full replay's checkpoint reset;
- the rebuild of a single projection.

Today they agree only because each repeats the same few lines. If one of them changes how it compares, orders,
deduplicates or reaches a factory-registered projection, the silo can start a reader that then retires, or reset
checkpoints the readers do not read. Nothing would report it. Code review of PR #219 found this.

Doing the same thing five times also has a cost. The nudge target is scoped, and the commit path resolves it. Every
command that commits facts therefore constructs every registered projection and its dependencies in its own scope,
only to learn their names.

## What Changes

The effect a consumer sees: none in behaviour. A host whose projections are expensive to construct pays for it once
per silo instead of once per commit.

- The projection registry the readers' retirement already uses becomes the one place the silo's projection names
  are worked out. It holds them in registration order, deduplicated and compared ordinally, and it maps each type
  to its name. It works them out once, under a lock, and does not keep a failure.
- The nudge target, the wait for a projection, the full replay's checkpoint reset and the rebuild ask the registry.
  The rebuild still resolves the instance it rebuilds. It now refuses an unknown name before it pauses any reader,
  with the same message as before.
- A store reader still resolves its projection instance per session run, under the entry's session, as the
  `projections` guarantees require. Only its "is this registered" question goes to the registry, as since #219.
- **Unchanged:** every published behaviour. Which projections are started, woken, waited for, reset and rebuilt
  stays the same. So do the names they read under, the messages a refusal carries, and every log event.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

_None._ No requirement changes, so the change sets `skip_specs: true`.

## Impact

- **Code:** `Stratara.Orleans`:
  - `Projections/ProjectionRegistrations.cs`;
  - `ProjectionNudgeTarget` in `Projections/ProjectionGrain.cs`;
  - `Projections/ProjectionProgress.cs`;
  - `Projections/ReplayCheckpointReset.cs`;
  - `Projections/ProjectionRebuilder.cs`.

  No public type, member or option changes. Grain interfaces are untouched.
- **Tests:** unit tests in `tests/Stratara.Orleans.Tests` for the registry's order, its deduplication and its
  name-by-type lookup, and for the commit path no longer constructing projections. The existing unit and
  integration tests for the wait, the rebuild, the replay, the seeding and the reset stay green unchanged.
- **Documentation:** `CHANGELOG.md` only, a *Changed* line about construction per commit. No page in `docs/`
  describes how names are worked out.
- **Release:** rides with the next patch. No release of its own.
- **Superseded sources:** `ProjectionProgress.ProjectionNames` (a private record) and the name computations in
  `ProjectionNudgeTarget`, `ReplayCheckpointResetTruncator.TruncateAllAsync` and
  `ProjectionRebuilder.RebuildAsync`.
