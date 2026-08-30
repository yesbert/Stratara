> **Status:** approved

# Dissolve the enhancement backlog — decided

The enhancement backlog was a second work queue. Every row was checked against the published
surface and decided on 2026-08-19. This change carries out the corrections and freezes the
file; nothing is left to decide.

## The nine rows still marked open, and what the code said

| Row | Backlog said | Code said | Decision |
|---|---|---|---|
| **T2-5** Event upcasting | open | **shipped** — `IEventUpcaster`, `IEventUpcasterPipeline`, chaining to fixpoint, cycle detection; specified as `event-schema-evolution` | correct the record |
| **T2-6** Projection helpers | open | truncator, replay state and progress ship; the idempotent-update helper does not — and the framework hand-writes it five times in `TenantProjection` | **kept** → `add-projection-idempotency-helpers` |
| **T2-11** RFC-7807 handler | open | only the 403 mapping ships | **kept** → `add-problem-details-exception-handler` |
| **T2-8** Job progress | open | submit and status ship (`IBackgroundTaskQueue`, `BackgroundTaskInfo`); progress and cancel do not | dropped |
| **T2-9** Streaming queries | open | nothing | dropped |
| **T3-12** `dotnet new` templates | open | nothing | dropped |
| **T3-13** SignalR projection push | open | the connection identity ships, no push glue | dropped |
| **T3-14** Kafka outbox | open | nothing | dropped |
| **T3-15** Handler-registration generators | open | nothing | dropped |

Everything else in the file — T1-1 through T1-4, T2-7, T2-10, T2-12 — is shipped and was already
marked so.

## Why the six were dropped

Four of them fail on a specific ground rather than on priority, which is why they are dropped rather
than deferred:

- **T3-13 SignalR push** contradicts a stated boundary. `architecture.md`: channel-specific glue
  stays in the consumer. Shipping a hub would not be a prioritisation call, it would be an
  inconsistency.
- **T2-9 streaming queries** would add a third dispatch shape to the mediator, and therefore a third
  arity to **every** pipeline behaviour — validation, tenant isolation, resilience, auditing, four
  specified capabilities. The evidence is one consumer building a retrieval product. Revisit if a
  second one asks.
- **T3-15 handler-registration generators** would have to reproduce the discovery semantics now
  specified in four capabilities — including that saga handler discovery finds non-public methods —
  or change a published guarantee. A source-generator package was already dropped once, in 3.0.14.
- **T2-8 job progress** — "progress" is not definable for an arbitrary delegate; only the job knows
  it. Submit and status ship; a consumer builds the rest on `IBackgroundTaskQueue` in a few lines.

Two are ordinary priority calls:

- **T3-12 templates** — nine samples with smoke tests serve the same purpose and age better.
- **T3-14 Kafka** — the transport abstraction is proven by two implementations. A third is market
  expansion nobody has asked for, and it would have to be dragged along by every messaging change.

## The observation worth keeping

The backlog's stated driver was *visible active maintenance* — an adoption goal, not a technical one.
Measured against that, the strongest available move is not in this file: it is
`compose-erasure-sweeps`, already in the queue. A framework whose headline capability is
crypto-shredding for erasure, and which offers no way to *perform* an erasure, has more to gain
there than from any transport.

## Impact

- The enhancement backlog — corrections applied, then frozen as chronicle. It was already marked
  frozen during the migration; this change makes the marking true by giving every row a home first.
