# Keep the replay flag in memory

> **Status:** approved

## Why

Every host that shares the replay coordination state over Redis answers "is a replay active?" with a
blocking round trip to Redis, on the calling thread, and the Orleans execution model asks on every hot
path: once per dispatched command, once per commit before the readers are nudged, twice per store-reading
grain per catch-up, and once per drain pass. A host with three projections makes roughly eight to ten
blocking Redis calls per command, several of them inside grain turns, although no replay ever runs. On a
small host — two cores, the normal shape of a container beside the product it serves — a few of those
calls block the thread pool's starting workers, the Redis reply cannot be processed because that needs a
pool thread too, and the multiplexer times out after five seconds. A consumer's nightly end-to-end runs
showed it: every failed run carried dozens of `RedisTimeoutException`s on `GET
stratara:projection:replay:active` with `sync-ops` in the dozens against `async-ops: 1`, surfacing as
failed catch-ups, read models that lag, grain registrations that fail and requests that end in 500 — a
different test each night, the signature of starvation rather than of one defect. The same code passes
on a developer machine, where a large thread pool hides it.

## What Changes

The consumer-visible effect: no path of the framework waits on Redis to learn whether a replay is active.
The answer comes from the host's memory; a change of the marking reaches every host over the coordination
store's channel and, as the safety net, through a periodic asynchronous refresh.

- The Redis-backed replay state answers `IsReplayActive` from a field. A marking that begins or ends in
  the host itself is seen there at once. A marking that begins, ends or lapses elsewhere is seen within
  the refresh period, and in the usual case within the latency of a published message.
- New `ProjectionReplayOptions.RefreshSeconds` (default 5), read from the `ProjectionReplay` section like
  the lease; refused when the host starts at zero or below, or at or above the lease.
- A host that cannot reach the coordination store keeps the last answer it had — before, the paths that
  asked failed with the connection's exception. The loss and the recovery are logged once each
  (`104_015`, `104_016`).
- Until the host first learns the marking, it answers that no replay is active.
- The interface and its call sites are unchanged. The in-process state, and a host's own
  `IProjectionReplayState`, are not touched.

Not changed: what the marking means, the lease, the progress counters, the replay-request channel, and
`GetProgress`, which stays a read of the coordination store because it is the dashboard's authoritative
view and not on any hot path.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `projections`: a new requirement, *Whether a replay is active is answered without waiting on the
  coordination store*; *Publication is suppressed while a replay is active* gains the bound within which
  another host's suppression begins.
- `host-composition`: *A setting that names its configuration section is read from it* gains the refresh
  period's validation beside the lease's.

## Impact

- `src/Stratara.Outbox.RabbitMQ/Projections/ProjectionReplayState.cs` — the field, the state channel,
  the refresh loop, disposal.
- `src/Stratara.Outbox.RabbitMQ/Projections/ProjectionReplayOptions.cs`,
  `ProjectionReplayOptionsBinding.cs` — `RefreshSeconds` and its validation.
- `src/Stratara.Outbox.RabbitMQ/DependencyInjection/OutboxServiceCollectionExtensions.cs` — the
  registration hands the state its clock and logger.
- `src/Stratara.Outbox.RabbitMQ/Diagnostics/Extensions/LoggerOutboxExtensions.cs`,
  `src/Stratara.Diagnostics/LogEvents.cs` — `104_015`, `104_016`.
- `src/Stratara.Abstractions/Abstractions/Projections/IProjectionReplayState.cs` — the documentation of
  `IsReplayActive` states what an implementation promises.
- `tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Projections/ProjectionReplayStateTests.cs`,
  `tests/Stratara.Outbox.RabbitMQ.Tests/DependencyInjection/ProjectionReplayOptionsBindingTests.cs`,
  `tests/Stratara.Documentation.Tests/OptionsSectionBindingTests.cs`.
- `docs/guides/write-a-projection.md`, `docs/guides/operate-the-orleans-execution-model.md`,
  `docs/reference/di-extensions-cheatsheet.md`, `CHANGELOG.md`.
- No package, dependency or tier changes. A patch release; a consumer adopts it by a version bump.
