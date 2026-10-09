# Keep the outcome of the last replay

> **Status:** approved

## Why

After a replay that succeeded, `IProjectionReplayState.GetProgress()` answers exactly what it answers
before any replay: inactive, 0 of 0, no message. A status reader polls, so it sees a replay only if one
of its polls falls inside the run. A consumer reported it on 2026-10-09 (consumer finding F-020): a replay
of 730 events began 41 ms after the operator's request and ended two seconds later; the first poll came a
second after that, read inactive and 0/0, and the operator's interface reported after 30 s that the replay
had not begun. The operator's natural next step — request it again — empties the read store a second time.
A failed replay keeps its message, so failure is observable and success is not.

Mapping the code for this finding turned up a second defect on the same path. Every host that registers
the replay worker subscribes to the request channel, and nothing claims a request: with the coordination
state in Redis, **one request starts a full replay in every such host at once**, all truncating and
rebuilding the same read store. Two requests in quick succession do the same within one host. Neither is
recorded anywhere.

## What Changes

The consumer-visible effect: a status reader can tell a replay that finished from one that never ran, can
tell its own replay from an older one, and a request starts exactly one replay.

- **The last outcome is kept.** `ReplayProgress` gains `LastReplay` — a new `ReplayOutcome` record with
  the request's id, when the replay started and ended, how many events it replayed, and whether it
  `Succeeded`, `Failed` or was `Interrupted` (host shutdown), with the failure message. It is kept after
  the replay ends and replaced when the next one ends, so it is readable while the next one runs.
  `ReplayProgress.RequestId` names the replay that is running.
- **A request has an identity.** New `IProjectionReplayState.RequestReplay(Guid requestId)`: the caller
  chooses the id and finds it again on `RequestId` and `LastReplay.RequestId`. The parameterless
  `RequestReplay()` stays and draws an id itself.
- **A request runs once.** Among the hosts that share the coordination state, exactly one runs a given
  request; the others pass it over. A request that arrives while a replay is running starts nothing and
  is logged (`104_019`), instead of starting a second rebuild on top of the first.
- **Shutdown is recorded as an interruption, not as a completion.** Today a replay cancelled between
  batches logs "completed" with its partial count. It now ends as `Interrupted` and logs `104_020`.
- **Progress reads the same on both coordination states.** After a failure, the Redis-backed state kept
  showing the failed run's counters until their lease ran out, while the in-process one and the guide
  showed 0 of 0. Both now show 0 of 0 once the replay has ended; the count lives on `LastReplay`.

Not changed: `ErrorMessage` on `ReplayProgress` (kept until the next replay starts, as today), the lease,
the refresh, `IsReplayActive`, and what a replay does to the read models.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `projections`: *A replay is requested, not scheduled* gains that a request carries an identity and is
  run once; *A replay reports progress and failure* gains the outcome that outlives the replay and the
  interrupted ending.

## Impact

- `src/Stratara.Abstractions/Abstractions/Projections/IProjectionReplayState.cs` — `ReplayOutcome`,
  `ReplayResult`, the two `ReplayProgress` properties, and four members with default implementations:
  `RequestReplay(Guid)`, `SubscribeToReplayRequestAsync(Func<Guid, Task>, …)`, `TryActivate(Guid)`,
  `Complete(ReplayCompletion)`.
- `src/Stratara.Outbox.RabbitMQ/Projections/ProjectionReplayState.cs` — the request id on the channel,
  the claim, the outcome key, `GetProgress`.
- `src/Stratara.Outbox.RabbitMQ/Projections/InProcessProjectionReplayState.cs` — the same in memory.
- `src/Stratara.Projections/Services/ProjectionReplayWorker.cs` — claims before it runs, completes with
  an outcome, tells an interruption from a completion.
- `src/Stratara.Diagnostics/LogEvents.cs`, `src/Stratara.Projections/Diagnostics/Extensions/LoggerProjectionExtensions.cs`
  — `104_019`, `104_020`.
- `docs/guides/write-a-projection.md` (*Watch a replay*, the table and the endpoint sample),
  `CHANGELOG.md`.
- No package, dependency or tier changes. Additive: a patch release.
