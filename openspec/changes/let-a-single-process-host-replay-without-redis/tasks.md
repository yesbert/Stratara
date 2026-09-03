## 1. The in-process implementation

- [x] 1.1 `src/Stratara.Outbox.RabbitMQ/Projections/InProcessProjectionReplayState.cs` (new,
      `internal sealed`): active flag, processed/total counters, error message and subscriber list
      under a private lock; `Activate` clears the error and stamps a lease `LeaseSeconds` ahead;
      `SetProgress` updates the counters and renews the lease; `IsReplayActive` and `GetProgress`
      treat an expired lease as inactive; `SetFailed` clears the marking and keeps the message;
      `Deactivate` clears all four; `RequestReplay` invokes every callback registered through
      `SubscribeToReplayRequestAsync`, and a callback that throws or faults is logged (`104_013`)
      without stopping the others. Takes `IOptions<ProjectionReplayOptions>`, `TimeProvider` and an
      optional logger.
- [x] 1.2 Tests in `tests/Stratara.Outbox.RabbitMQ.Tests/Projections/InProcessProjectionReplayStateTests.cs`
      (new): inactive by default; activate → active with zero progress; progress and percentage,
      including total zero → 0 %; `SetFailed` → inactive with the message; `Deactivate` → clean;
      lease expiry via a fake `TimeProvider` → inactive; `RequestReplay` fires each subscriber once.

## 2. The selection

- [x] 2.1 `src/Stratara.Diagnostics/LogEvents.cs`: `Projection.ProjectionReplayCoordinationInProcess = 104_012`
      with a summary. Source-generated `LogProjectionReplayCoordinationInProcess(this ILogger)` at
      `Warning` in `src/Stratara.Outbox.RabbitMQ/Diagnostics/Extensions/LoggerOutboxExtensions.cs`.
- [x] 2.2 `src/Stratara.Outbox.RabbitMQ/DependencyInjection/OutboxServiceCollectionExtensions.cs`:
      `AddProjectionReplayState()` try-adds a singleton factory that resolves
      `IConnectionMultiplexer` with `GetService`; present → `ProjectionReplayState`, absent →
      `InProcessProjectionReplayState` after logging `104_012`. Register `TimeProvider.System` with
      try-add for the fallback. Rewrite the XML summary and remarks: which is chosen when, that the
      choice is made at first resolution so call order does not matter, and what the fallback
      confines.
- [x] 2.3 Tests in `tests/Stratara.Outbox.RabbitMQ.Tests/DependencyInjection/OutboxServiceCollectionExtensionsTests.cs`:
      without `IConnectionMultiplexer` the state resolves as the in-process type and the warning is
      logged once across two resolutions; with a mocked multiplexer registered before, and one
      registered after, `AddProjectionReplayState()` the state resolves as `ProjectionReplayState`
      and no warning is logged; a consumer-registered `IProjectionReplayState` wins in both orders.
- [x] 2.4 `tests/Stratara.EventSourcing.WorkerDefaults.Tests/WorkerDefaultsCompositesTests.cs`: each
      composite that carries a dispatcher builds a provider **without** Redis and resolves
      `IProjectionReplayState`; the existing Redis-mocked cases stay.
- [x] 2.5 `src/Stratara.Infrastructure/DependencyInjection/CachingServiceCollectionExtensions.cs`:
      the remarks say what `AddCaching()` makes possible (multi-replica outbox lock, replay that
      spans hosts) instead of what fails without it.

## 3. Documentation and changelog

- [x] 3.1 `docs/guides/write-a-projection.md`, replay section: one host needs no Redis; a replay
      that must suppress publication in other hosts needs the shared store; name the warning id.
- [x] 3.2 `docs/getting-started/prerequisites.md` and `docs/getting-started/di-composition.md`:
      Redis listed as optional, with the two things it enables.
- [x] 3.3 `CHANGELOG.md` `[Unreleased]` → *Fixed*: composites no longer require Redis to start; the
      in-process fallback, its confinement and warning `104_012`; Redis-backed hosts unchanged.
- [x] 3.4 Regenerate `llms-full.txt` if the registration summaries or log-event inventory changed.

## 4. Gate

- [x] 4.1 `./scripts/local-gauntlet.sh` green; `openspec validate --strict` clean.
