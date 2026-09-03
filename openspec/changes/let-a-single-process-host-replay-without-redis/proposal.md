> **Status:** approved

# Let a single-process host replay without Redis

## Why

Every host composite that carries a dispatcher — the backend, the command worker, the saga worker,
the outbox worker and the projection worker — registers the projection-replay state, and the only
implementation of it talks to Redis. The connection it needs is registered by one call no composite
makes and no getting-started page mentions. A consumer who composes a host exactly as documented,
with PostgreSQL and RabbitMQ in place, fails at the first dispatch with a dependency-injection error
about a Redis connection multiplexer. The smallest honest deployment of the framework is therefore
three pieces of infrastructure, and the third is there to coordinate a maintenance operation the
consumer has not asked for yet.

The outbox lock already solved the same problem the right way: the default is an in-process lock
that is correct for one replica, and a multi-replica deployment opts into the Redis-leased one. The
replay state should follow that shape. Found on 2026-09-03 while briefing the first example that
consumes the published packages; third step of the entry-point work approved that day.

## What Changes

- **A host without Redis starts and dispatches.** Where no Redis connection is registered, the
  replay state is held in process: the active marking, the progress counters, the failure message
  and the replay-request channel all live in that host.
- **A host with Redis behaves exactly as today.** Where a Redis connection is registered, the shared
  implementation is used and a replay is coordinated across every host that shares it.
- **The framework says which one it chose.** A host that falls back records a warning once at
  start-up: replay coordination is per process, and a replay requested here suppresses publication
  here only. An operator who runs several hosts learns from the log, not from a lost side effect,
  that shared coordination is missing.
- The documentation for replay and for host composition says that Redis is what makes a replay span
  hosts, and that a single-process host needs none.

What a replay does, when it runs, what it truncates and how it reports progress do not change. Only
where the coordination state lives when nothing shared is available changes, and that case failed
outright before.

## Capabilities

### New Capabilities

_none_

### Modified Capabilities

- `projections`: gains the requirement that replay coordination does not require shared
  infrastructure in a single-process host — the state is held in process where nothing shared is
  registered, shared where it is, and the host records which — and the requirement *Publication is
  suppressed while a replay is active* says that the suppression reaches every host only where the
  coordination state is shared.

## Impact

- `src/Stratara.Outbox.RabbitMQ/Projections/` — an in-process implementation beside the Redis one.
- `src/Stratara.Outbox.RabbitMQ/DependencyInjection/OutboxServiceCollectionExtensions.cs` —
  `AddProjectionReplayState()` chooses by the presence of a Redis connection; XML docs say so.
- `src/Stratara.Diagnostics/LogEvents.cs` — one new warning id in the projection range; the
  source-generated message in the outbox package's logger extensions.
- `src/Stratara.Infrastructure/DependencyInjection/CachingServiceCollectionExtensions.cs` — the
  remarks stop saying the replay state fails without this call and say what it gains from it.
- `docs/guides/write-a-projection.md` (replay section), `docs/getting-started/di-composition.md`,
  `docs/getting-started/prerequisites.md` — Redis is optional for one host, required for a replay
  that spans hosts and for a multi-replica outbox worker.
- `tests/Stratara.Outbox.RabbitMQ.Tests/`, `tests/Stratara.EventSourcing.WorkerDefaults.Tests/` —
  the selection, the in-process semantics and the composites resolving without Redis.
- `CHANGELOG.md` — `[Unreleased]`.
- Additive on the published surface: a patch release. Source: consumer briefing for
  `Stratara.Examples`, 2026-09-03 (`.claude/docs/examples-consumer-briefing.md`, item 2).
