## Context

See proposal.md, *Why*. Today these are the places that work out projection names:

| Where | Scope | How often |
|---|---|---|
| `ProjectionRegistrations` | singleton, own scope | once per silo (since #219) |
| `ProjectionNudgeTarget` | scoped, takes `IEnumerable<IProjection>` | each time a scope resolves it: silo start, wake-up grain activation, seeding, reset, and `OrleansEventBundleDispatcher` (scoped) on every dispatched bundle |
| `ProjectionProgress.ProjectionNames` | singleton, cached in a field | once per host, on the first wait |
| `ReplayCheckpointResetTruncator.TruncateAllAsync` | own scope | each full replay |
| `ProjectionRebuilder.RebuildAsync` | own scope | each rebuild, after the pause |

All of them do the same thing: they resolve `IEnumerable<IProjection>` and map each projection through
`IProjectionHandler.GetProjectionName`. Some apply `Distinct`, and only one of them uses an explicit ordinal comparer.

## Goals / Non-Goals

**Goals:**

- One computation of the silo's projection names, used everywhere a name list or a name-by-type lookup is needed.
- No projection constructed on the commit path just to learn names.

**Non-Goals:**

- Changing how a store reader resolves the projection instance it applies to. That resolution needs the entry's
  session in a scope of its own (`projections` capability), and a cached instance would break it.
- Doing for sagas what `SagaRegistrations` already does.
- Any change to names, messages, log events or grain interfaces.

## Decisions

### D1 — `ProjectionRegistrations` is the single source

The registry exposes:

- `Names`: the names in registration order, deduplicated and compared ordinally. This is the order `ProjectionNudgeTarget`
  keeps today, and `ConsumerNames` and the seeding and reset reports depend on it.
- `Contains(name)`.
- `NameOf(Type)`, which returns null for a type that is not registered.

It works them out once, under the lock #219 introduced, in a scope of its own, and keeps no failure.

*Alternative rejected:* keep `ProjectionNudgeTarget` as the source and have the others resolve it. It is scoped
because it takes the projections, which is the cost this change removes.

### D2 — `ProjectionNudgeTarget` takes the registry instead of the projections

Its constructor takes `ProjectionRegistrations` in place of `IProjectionHandler` and `IEnumerable<IProjection>`, and
`ConsumerNames` returns `Names`. It stays scoped, because `INudgeTarget` is resolved as a set of scoped services
alongside the saga target, but resolving it no longer constructs projections.

*Evidence:* the scoped `OrleansEventBundleDispatcher` takes `IEnumerable<INudgeTarget>`
(`OrleansEventBundleDispatcher.cs`). A unit test verifies that resolving the dispatcher in a fresh scope does not
construct a projection.

### D3 — The wait, the replay reset and the rebuild ask the registry

- `ProjectionProgress` drops its private `ProjectionNames` record and its field. `WaitAsync(string)` checks
  `Contains`, and `WaitAsync<TProjection>()` uses `NameOf(typeof(TProjection))`. The refusal messages stay
  word for word.
- `ReplayCheckpointResetTruncator.TruncateAllAsync` takes `Names` instead of resolving the projections.
- `ProjectionRebuilder.RebuildAsync` checks `Contains(projectionName)` before it pauses the readers. It still resolves
  the instance afterwards in its own scope, to rebuild it and to check `IRebuildableProjection`. The refusal of an
  unknown name now comes before any reader is paused rather than after. The message is the same, and the caller
  cannot tell the difference except that no pause and resume happens.

### D4 — Working names out once is safe for every projection that runs today

The registry constructs each projection once, in a scope without a session. The silo's start already does the same,
because `StoreReaderGrainStarter` resolves the nudge targets in a scope without a session. A projection that cannot
be constructed that way therefore already fails the silo's start, and no host that runs today is affected. A
projection's constructor that has side effects runs once per silo instead of once per commit. That is the intended
effect, and the `projections` capability promises nothing about how often a projection is constructed outside
applying.

## Risks / Trade-offs

- **[A host replaces `IProjectionHandler` with one whose names depend on scoped state]** The registry caches the first
  answer. → The names key checkpoints and reader grains, so names that change between scopes already break the
  readers. No new exposure.
- **[The registry is built on the commit path the first time]** The first command after start can pay for
  constructing every projection once. → The silo's start resolves the nudge targets first in practice, and does so
  on every silo, so the cost lands at start.

## Migration Plan

None. Internal only. Rolling deployments are unaffected, because no grain interface or message changes.
