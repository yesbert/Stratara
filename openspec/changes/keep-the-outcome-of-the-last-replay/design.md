## Context

`IProjectionReplayState` (`src/Stratara.Abstractions/Abstractions/Projections/IProjectionReplayState.cs`)
is a published abstraction with two internal implementations in `Stratara.Outbox.RabbitMQ`: the
in-process one (fields under a lock) and the Redis-backed one (keys `stratara:projection:replay:active`,
`…:processed`, `…:total`, `…:error`; request channel `…:request` with payload `"replay"`; state channel
`…:state`). `ReplayProgress` is a positional record `(IsActive, ProcessedEvents, TotalEvents, Percentage,
ErrorMessage)`.

`ProjectionReplayWorker.ExecuteAsync` (`src/Stratara.Projections/Services/ProjectionReplayWorker.cs`)
subscribes a `Func<Task>` and, on every message, runs `RunReplayAsync`: `Activate` → clear forgotten
tenants → truncate → `SetProgress(0, total)` → batches → `finally Deactivate`; a failure reaches the
catch after the `finally` and calls `SetFailed`. Nothing claims a request: every host that called
`AddProjectionHandling` subscribes, and Redis pub/sub delivers to all of them. Cancellation between
batches leaves the loop normally and logs `104_006` "completed" with the partial count.

`Deactivate` deletes the counters; `SetFailed` (Redis) deletes only the active key, so the counters stay
readable until their lease lapses, while the in-process `GetProgress` reports 0/0 whenever the lease is
not alive — the two disagree after a failure, and the guide's table matches the in-process one.

Nothing in `src/` or `samples/` calls `RequestReplay()` or `GetProgress()`; the guide shows a
consumer-written endpoint pair. Test doubles of the interface exist as hand-written classes
(`ProjectionReplayStreamOrderTests` ×2, `ResumedOnceInOrderTests`) and as Moq mocks.

Evidence: the implementation at `main` 398d20d; consumer finding F-020 (2026-10-09, timings of request,
replay and first poll); the double run is read off the subscription code, not observed in a log.

## Goals / Non-Goals

**Goals:**
- A poller can see that a replay ran, how it ended, and whether it was the one it asked for.
- One request, one replay, across every host sharing the coordination state.
- Both coordination states report the same progress at every point of a replay's life.
- Source and binary compatibility for callers and for implementers of the interface.

**Non-Goals:**
- A history of replays. One outcome — the last — answers the poller; a log has the rest.
- Queuing a request that arrives during a replay. A replay already rebuilds from the whole store; a
  queued second one would only empty what the first just built.
- A framework-shipped status endpoint. The framework still ships none; the guide's sample changes.
- Changing what a failed replay leaves in the read models — that is
  `restore-the-read-models-when-a-replay-fails`, which builds on the outcome introduced here.

## Decisions

**New members with default implementations; nothing changes signature.** Changing `void RequestReplay()`
to return an id is a binary break for every compiled caller. Instead:
- `void RequestReplay(Guid requestId)` — default: `RequestReplay()` (the id is lost on an implementation
  that does not override it; both framework implementations do).
- `Task SubscribeToReplayRequestAsync(Func<Guid, Task> onReplayRequested, CancellationToken)` — default:
  subscribes `() => onReplayRequested(Guid.Empty)` through the existing member. The overloads differ in the
  lambda's arity, so no existing call becomes ambiguous.
- `bool TryActivate(Guid requestId)` — default: `if (IsReplayActive) return false; Activate(); return true;`
- `void Complete(ReplayCompletion completion)` — default: `SetFailed` for `Failed`, `Deactivate` otherwise.
  `ReplayCompletion` is a sealed record `(ReplayResult Result, long ReplayedEvents, string? ErrorMessage)`
  rather than three parameters, so `restore-the-read-models-when-a-replay-fails` can add what it reports as
  an `init` property without another overload (revised during implementation).
`Activate`, `Deactivate` and `SetFailed` stay: a consumer's reset endpoint calls `Deactivate` today.
- *Alternative:* a new `IProjectionReplayCoordinator` beside the state. Rejected: two abstractions for one
  state machine, and every host would need both registered.

**`ReplayProgress` grows by `init` properties, not positional parameters.** `RequestId` (`Guid?`) and
`LastReplay` (`ReplayOutcome?`). A positional parameter changes the constructor and `Deconstruct`, which
breaks compiled callers; an `init` property does not. `ReplayOutcome` is a sealed record
`(Guid RequestId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, long ReplayedEvents, ReplayResult
Result, string? ErrorMessage)`, `ReplayResult` an enum `Succeeded, Failed, Interrupted`.

**The state stamps the times, the worker reports the result.** `TryActivate` records the request id and
the start from the state's `TimeProvider`; `Complete` stamps the end and writes the outcome. The worker
never handles a clock, and a test drives both through `FakeTimeProvider`. The in-process state already
takes a `TimeProvider`; the Redis one does too.

**The claim is one atomic script on Redis.** `TryActivate(requestId)` runs a Lua script that (1) sets
`…:claimed:{id}` with `NX` and a one-day expiry and returns 0 if it existed — another host took this
request; (2) sets `…:active` to `"true"` with `NX` and the lease and returns 0 if it existed — another
replay is running; (3) deletes `…:error` and sets `…:request-id` and `…:started` with the lease. The
active key keeps the value `"true"`, so a host on the previous version reading it during a rolling upgrade
still sees the marking. A host that loses the claim logs nothing (it is the normal case for every host but
one); a host that wins the claim but finds a replay active logs `104_019`.
- *Alternative:* `SET NX` on the active key alone. Rejected: a host that receives the message after a
  short replay has already ended finds the key free and runs the same request a second time — exactly the
  F-020 timing (two seconds).

**The outcome lives in one unleased hash.** `Complete` writes `…:last` (fields per `ReplayOutcome`) and
deletes `…:active`, `…:processed`, `…:total`, `…:request-id`, `…:started`; for `Failed` it sets `…:error`
as `SetFailed` does today, for the others it deletes it. `GetProgress` reads all keys in one `MGET` plus an
`HGETALL` and reports counters only while active. No lease on the outcome: it suppresses nothing, so a
stale one costs nothing, and the next `Complete` replaces it.

**The request channel carries the id.** `RequestReplay(id)` publishes `id.ToString("N")`;
`RequestReplay()` publishes `Guid.CreateVersion7()`. A subscriber that receives the legacy payload
`"replay"` from a host on the previous version draws its own id — so in a mixed fleet each new host claims
a different id and the double run persists until every host is upgraded. Documented in the changelog's
upgrade note.

**Interrupted is decided in the worker.** `OperationCanceledException`, or the loop leaving with the token
cancelled, ends as `Complete(new ReplayCompletion(Interrupted, replayedSoFar))` and logs `104_020` instead of `104_006`. A
failure ends as `Complete(new ReplayCompletion(Failed, replayedSoFar, message))`; the `finally` no longer calls `Deactivate`, so
the outcome is written once, by whichever ending happened. The replayed count is tracked in the worker
across batches so a failure can report it.

## Risks / Trade-offs

- [A request whose claiming host dies before `TryActivate` returns is never run] → The claim expires after
  a day; the requester sees no `RequestId` appear and requests again with a new id. Documented.
- [The `Complete` after a host-shutdown runs while the multiplexer is closing] → Best effort: a failed
  write leaves the active marking to lapse as today, and the outcome stays the previous one. Logged by
  the existing replay-failure path.
- [Clock skew between hosts makes `StartedAt` of one host and `EndedAt` of another incomparable] → Both are
  stamped by the replaying host.
- [Hand-written test doubles of the interface] → Default implementations keep them compiling; the three
  in this repository are updated where a test depends on the new behaviour.

## Migration Plan

A version bump. Rolling upgrade: until every host runs the new version, a request from an old host can
still start one replay per new host (legacy payload), and old hosts ignore the claim. Rollback leaves the
`…:last`, `…:claimed:*`, `…:request-id` and `…:started` keys behind, which the old version never reads;
the claims expire on their own.
