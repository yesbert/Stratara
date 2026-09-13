# Prove an Orleans execution model

> **Status:** approved

## Why

Stratara runs asynchronous work through bus workers: a command consumer, a projection worker, a saga
worker and an outbox drain. Each of them solves, by hand and with a weaker guarantee, a problem that a
virtual-actor runtime solves as its core feature:

| Job | Bus workers in 4.0.3 | Orleans |
|---|---|---|
| One writer per aggregate | a bucket lock that holds inside one worker in one process; on RabbitMQ a concurrency conflict is requeued without bound | one activation per key, turn-based |
| Process manager | `ISaga` is stateless by contract — a new instance per bundle, no correlation store, no timeouts | a grain keyed by correlation, rehydrated by a fold, with a durable timer |
| Timeouts and wake-ups | left to the consumer — typically a hosted service polling under a lock | reminders, persisted, firing once per cluster |
| Singleton work | a distributed lock (the outbox drain) or a deployment assumption | one grain for the key |
| Projections | push over the bus, no checkpoint; catch-up only through a full replay that truncates every read model | a grain per projection reading the store from its own checkpoint |
| Bounded heavy work | a separate heavy-command topic with its own parallelism | a bounded stateless-worker grain |

Two delivery gaps in 4.0.3 sharpen the question. A committed bundle whose publish never happens is lost
to every projection and saga (SF-002), and a handler failure on RabbitMQ discards the message rather than
dead-lettering it (SF-001). A checkpoint reader closes the first by construction — provided it reads in
commit order, which the store does not offer today.

**Why now:** a consumer building on 4.0.3 co-hosts Orleans with its API and has deferred its next slices
until this question is answered, and SF-001 and SF-002 are live in the released version.

The question this change answers: **should Stratara offer Orleans as an execution model, and should it
become the recommended one**, with a migration a consumer can take without rewriting its handlers,
projections and sagas.

## What Changes

- A proof of concept in a new, **non-packable** project (working name `Stratara.Orleans`, Tier-C) with its
  own host composite, building six blocks far enough to run the correctness tests and benchmarks in
  `design.md`: an owner-checked durable timer, singleton work, an aggregate grain, a projection grain, a
  saga grain and a heavy-work grain.
- A port for reading the event store in commit order (working name *committed position reader*), with a
  naive baseline, a safety-window baseline, a portable counter and a PostgreSQL-native implementation.
- Correctness tests, and benchmarks against the bus workers on the same hardware. Every measurement's
  expectation and falsification criterion is committed **before** its first run, and its raw output is
  committed with the result, inside this change.
- A written recommendation, a migration note for an existing consumer, and a recorded decision for
  SF-001, SF-002 and SF-003.

## Consumer-visible effect

**None in this change.** No published package gains, loses or changes a member; no existing composite
registers anything different; no package version moves; nothing is published. In particular this change
does not change:

- what `ICommandOutboxDispatcher`, `IEventSource`, `IProjection` or `ISaga` promise;
- the RabbitMQ or Azure Service Bus failure mapping (SF-001 is decided here, fixed elsewhere);
- the event store schema a consumer migrates to (any column the native reader needs lives in a PoC-only
  model until a follow-up change ships it with a spec delta and a migration note).

Productising any result — a packable package, a store schema change, a spec delta, a deprecation of the
bus workers — is a follow-up change of its own, with its own approval.

## Capabilities

### New Capabilities

None. The change carries `skip_specs: true`: it produces evidence and a recommendation, not a guarantee
a consumer can observe. Specifying the Orleans path now would be specifying behaviour that the proof of
concept exists to decide.

### Modified Capabilities

None. The proof of concept is measured against these capabilities and must keep every guarantee they
state on its own path: `projections`, `sagas`, `outbox-and-messaging`, `mediator-dispatch`,
`host-composition`, `event-sourcing-store`.

## Impact

- **New, non-packable:** `src/Stratara.Orleans/`, `tests/Stratara.Orleans.Tests/`,
  `tests/Stratara.Orleans.IntegrationTests/` (Testcontainers; outside the local gauntlet),
  `tests/Stratara.Orleans.Benchmarks/`.
- **New evidence directory:** `openspec/changes/prove-an-orleans-execution-model/evidence/` —
  pre-registered expectations, raw benchmark output, hardware and version records. It travels into the
  archive with the decision.
- **Dependencies:** `Directory.Packages.props` gains the Orleans packages the proof of concept needs,
  pinned to one version (10.3.1 at the time of writing). No packable project references them.
- **Not touched:** `Stratara.Publish.slnf`, `<VersionPrefix>`, `CHANGELOG.md`, every existing composite.
- **Findings recorded:** SF-001 (RabbitMQ discards a failed message), SF-002 (commit and publish are two
  transactions), SF-003 (concurrency detection depends on a PostgreSQL exception). They reached the
  project as a hand-off from a consumer team together with the briefing for this proof of concept;
  neither document is in this repository, so `design.md` carries their substance, verified against
  `main` at `1476af9`.
- **Superseded sources:** none in this repository.
