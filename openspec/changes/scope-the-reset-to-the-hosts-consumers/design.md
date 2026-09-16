## Context

The reset is implemented in `Stratara.Orleans.EntityFrameworkCore` (`Hosting/ExecutionModelReset.cs`).
Reminders and membership are deleted with a filter on the cluster's `ServiceId` and `ClusterId`; the
checkpoints with an unfiltered `ExecuteDeleteAsync` on the read context (line 38). A checkpoint row is
keyed by consumer name and partition only.

The consumer names a host reads under are known in `Stratara.Orleans`, and only internally:

- Projections: `IProjectionHandler.GetProjectionName` over the registered `IProjection`s — the same
  enumeration `ProjectionNudgeTarget` and `ReplayCheckpointResetTruncator` already use to address the
  store-reader grains and to zero their checkpoints before a replay.
- Sagas: the constant `SagaGrain.ConsumerName` (`"sagas"`), served when `AddStrataraSagaGrains`
  registered `SagaNudgeTarget`.

`Stratara.Orleans` grants no friend access to `Stratara.Orleans.EntityFrameworkCore` today. Friend
access between shipped, lockstep packages has precedent: `Stratara.Mediator` → `Stratara.Outbox.RabbitMQ`,
`Stratara.Outbox.RabbitMQ` → `Stratara.Infrastructure`.

## Goals / Non-Goals

**Goals:**
- The reset's checkpoint deletion is filtered to the names the host's store readers use, derived from
  the same registrations that start those readers, so the two cannot drift apart.
- No schema change and no public surface.

**Non-Goals:**
- A deployment discriminator on the checkpoint row. Owner decision 2026-09-16: it would fix the shared
  saga consumer as well, but costs a migration for every Orleans host and a transition for existing
  rows; the specification states the sharing limit instead.
- Detecting at run time that two deployments write one consumer's checkpoints.
- Cleaning up checkpoints of consumers the host no longer registers.

## Decisions

### D1 — Scope by the host's registered consumer names

The reset deletes checkpoints whose consumer is one of the host's store-reading projection names, plus
the saga consumer when saga grains are registered.

*Rejected: a deployment column on the checkpoint row* — see Non-Goals.
*Rejected: stating "one read store per deployment" and leaving the reset total* — keeps the surprise
for any host that shares a read store with a service outside the execution model, which is the case
the finding was about.

Evidence: `ReplayCheckpointReset.cs` (the replay path already scopes by the same names);
`ExecutionModelReset.cs:38`.

### D2 — One internal source of consumer names in `Stratara.Orleans`

`Stratara.Orleans` gets an internal service that yields the host's store-reader consumer names, built
from the registered `INudgeTarget`s: each target exposes the consumer names it addresses
(`ProjectionNudgeTarget` its projection names, `SagaNudgeTarget` the saga consumer). The replay
truncator switches to it where it enumerates projection names today, so every place that needs "which
checkpoints are this host's" reads one list. `Stratara.Orleans.csproj` grants
`InternalsVisibleTo` to `Stratara.Orleans.EntityFrameworkCore`.

*Rejected: a public port (for example on `IProjectionCheckpointStore` or a new interface)* — the names
are an implementation detail of how the execution model keys its readers; publishing them adds surface
a consumer would have to keep compatible, for one internal caller.
*Rejected: re-deriving the names inside the EF package from `IProjection` registrations* — would miss
the saga consumer or hard-code its name a second time.

Evidence: `ProjectionGrain.cs:191-220`, `SagaGrain.cs:66,96-106`; `Stratara.Mediator.csproj:30`.

### D3 — A composition without consumers removes no checkpoint

If the reset runs in a service provider that registers no store-reading consumer, it deletes no
checkpoint and reports zero. It does not throw: a host without store readers is legitimate. The
operations guide says the reset must run from the host's own composition — the same one that
registers its projections and sagas — which is how the existing integration test already runs it.

Evidence: `tests/Stratara.Orleans.IntegrationTests/Hosting/ResetTests.cs` resolves the reset from the
host's `app.Services`.

## Risks / Trade-offs

- [An operator ran the reset from a tool that registers no projections, expecting a clean read store]
  → the report shows zero checkpoints removed, and the guide names the composition requirement.
- [Checkpoints of a removed projection now outlive a reset] → inert, since nothing reads them; the guide
  says so and that deleting them is the host's plain delete.
- [Two deployments running sagas on one read store keep colliding] → unchanged by this change; the
  specification now names it unsupported.

## Migration Plan

Patch release. No schema change; a host upgrades without action. Rollback is the previous package.
