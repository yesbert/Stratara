# Design — Let a single-process host replay without Redis

## Context

See `proposal.md` → *Why*. What matters here is who resolves the replay state, what the Redis
implementation does, and the precedent the outbox lock sets.

`IProjectionReplayState` (`src/Stratara.Abstractions/Abstractions/Projections/IProjectionReplayState.cs`)
has one implementation, `ProjectionReplayState` in `src/Stratara.Outbox.RabbitMQ/Projections/`,
`internal sealed`, constructed from `(IConnectionMultiplexer, IOptions<ProjectionReplayOptions>)`.
It keeps four keys under `stratara:projection:replay:*` — active (leased), processed and total
(leased, renewed by `SetProgress`), error (not leased) — and a pub/sub channel for
`RequestReplay` / `SubscribeToReplayRequestAsync`. Its remarks document the lease semantics: a host
that dies mid-replay stops renewing and the marking lapses.

`AddProjectionReplayState()` (`OutboxServiceCollectionExtensions.cs:53`) does
`AddOptions<ProjectionReplayOptions>()` and `TryAddSingleton<IProjectionReplayState, ProjectionReplayState>()`.
`AddOutboxDispatcher()` calls it, and every composite in `WorkerDefaultsHostBuilderExtensions.cs`
that carries a dispatcher calls `AddOutboxDispatcher()`; `AddEventProjectionWorkerServices()` calls
it directly. Three types depend on the state: `CommandOutboxDispatcher` and
`EventBundleOutboxDispatcher` consult `IsReplayActive` on every publish, `ProjectionReplayWorker`
drives the rest.

`IConnectionMultiplexer` is registered only by `AddCaching()`
(`src/Stratara.Infrastructure/DependencyInjection/CachingServiceCollectionExtensions.cs:51`), whose
own remarks already say that two components "take `IConnectionMultiplexer` directly" and that "the
coupling is invisible until one of them fails to resolve".

The precedent: `AddOutboxWorker()` does `TryAddSingleton<IOutboxLock, NullOutboxLock>()` and
`AddRedisOutboxLock()` replaces it. The docs say the null lock is "safe only for single-instance
deployments". Nobody has to run Redis to run one outbox worker.

`Stratara.Outbox.RabbitMQ` references `Abstractions`, `Contracts`, `Mediator`, `Sessions`, `Shared`
and the `StackExchange.Redis` package; it does not reference `Stratara.Projections`, so a type that
lives beside the Redis implementation cannot use the projection package's logger extensions.
`LogEvents.Projection` (`src/Stratara.Diagnostics/LogEvents.cs:67-81`) has `104_012` free.

## Goals / Non-Goals

**Goals:**

- A host that registers no `IConnectionMultiplexer` resolves every composite and dispatches.
- A host that registers one — before or after the composite — gets the Redis implementation and
  observes no change.
- The fallback says so, once, at a level an operator's dashboard shows by default.

**Non-Goals:**

- Cross-process coordination without Redis. A database-backed coordination store would be the
  principled alternative and is a separate change with its own trade-offs; this one removes a
  failure, it does not add a transport.
- Changing the Redis implementation, its keys, its lease or its channel.
- Making the outbox lock's default any louder. It is the precedent, not the subject.

## Decisions

### `AddProjectionReplayState()` registers a factory that selects by the presence of a Redis connection

The try-add becomes `TryAddSingleton<IProjectionReplayState>(sp => ...)` with a factory that asks
the provider for `IConnectionMultiplexer` with `GetService` and returns the Redis implementation when
one is present, the in-process one otherwise. Because the choice is made when the singleton is first
resolved — after the container is built — the order of `AddCaching()` and the composite does not
matter, which is the standing rule of the composition surface. The factory logs the fallback through
an `ILogger` resolved from the same provider; a singleton factory runs once, so the warning is
recorded once.

*Rejected: keep the constructor injection and add a second `TryAddSingleton` for an in-process
implementation guarded by `services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer))` at
registration time.* Order-dependent: a host that calls the composite first and `AddCaching()` second
would be locked into the in-process state with Redis sitting unused. The factory reads the built
container instead.

*Rejected: make `IConnectionMultiplexer` optional in the Redis implementation's constructor and
branch on null inside every member.* Two code paths through a class whose remarks document Redis
semantics, and a `null` that has to be checked in eight members forever.

*Rejected: throw a clearer exception naming `AddCaching()`.* Better than today's message, and still
a third container for a host that will never request a replay.

### The in-process implementation lives beside the Redis one and mirrors its observable semantics

`InProcessProjectionReplayState`, `internal sealed`, in `src/Stratara.Outbox.RabbitMQ/Projections/`,
holds the active flag, the two counters and the error under a private lock and keeps the subscribed
callbacks in a list; `RequestReplay` invokes each. It applies the same lease: `Activate` and
`SetProgress` stamp an expiry `LeaseSeconds` ahead, and `IsReplayActive` / `GetProgress` treat an
expired marking as inactive, so the *A replay reports progress and failure* and lease scenarios hold
with either implementation. `SetFailed` clears the marking and stores the message; `Deactivate`
clears everything except nothing — the same as the Redis one, which deletes all four keys.

Evidence: the Redis implementation's members (`ProjectionReplayState.cs:39-107`), the existing
`ProjectionReplayWorkerTests`, and new tests that run the in-process state through the same
sequence — activate, progress, percentage with total zero, failure, lease expiry via a `TimeProvider`.

*Rejected: put the in-process implementation in `Stratara.Testing`.* Test support is not
referenceable from a production host without a build error (`STRATARA1001`), by design.

*Rejected: put it in `Stratara.Abstractions` as a public type, like `BucketLockPool`.* Nothing
outside the outbox package needs to name it; the selection is the registration's job.

### The warning is a projection log event emitted from the outbox package

`LogEvents.Projection.ProjectionReplayCoordinationInProcess = 104_012`, `Warning`, with a
source-generated message in the outbox package's own logger extensions
(`src/Stratara.Outbox.RabbitMQ/Diagnostics/Extensions/LoggerOutboxExtensions.cs`). The text
names the consequence and the remedy: replay coordination is confined to this process; register a
Redis connection (`AddCaching()`) for a replay to span hosts.

## Risks / Trade-offs

- [An operator runs several hosts without Redis, requests a replay in the projection worker, and the
  API host keeps publishing] → This is the documented consequence, and it is announced at start-up
  in every host. Before this change the same deployment did not start at all, which is not a safer
  state — it is a state nobody ships. The spec delta says the suppression reaches shared hosts only.
- [The fallback masks a forgotten `AddCaching()` in a deployment that meant to have Redis] → The
  warning is the signal; it fires in every host that lacks the connection, on every start.
- [Two implementations drift in semantics] → One test sequence runs against both where a Redis
  double is available; the lease and percentage rules are shared by test, not by inheritance.
