# Why Event Sourcing

## The default everyone knows

CRUD storage keeps the **current state** of an entity. Updates overwrite the previous value. History, if anyone wants it, gets stitched together from audit-log triggers and hopes nobody dropped them. It's the default for a reason — fast to build, easy to reason about for the in-the-moment query "what does this row look like right now?"

Event sourcing flips the model. The persisted truth is the **sequence of facts** that produced the current state. `current state = fold(events)`. The current value is derived; the events are durable.

This sounds academic. It pays off in five concrete ways.

## 1. The audit trail is the storage model

There's no separate "audit log" to maintain, miss, or fall out of sync with the data. Every state change *is* a recorded event with timestamp, actor, and causation. "Who changed this customer's address on 2024-03-15?" is answered by a query against the event stream — not a Slack thread to whoever happened to be on call.

This is why heavily regulated domains — finance, healthcare, government — keep rediscovering event sourcing. The auditability is structural.

## 2. Replay rebuilds anything

A projection's job is to fold events into a read model. The read model is disposable: drop it, replay the events, get a fresh copy. This makes operations that are normally painful into trivial ones:

- **New report?** Write a new projection. Replay history. Get the new view backfilled.
- **Bug in a projection?** Fix the code. Drop the read store. Replay. The bug-introduced data is gone.
- **Migrating the read model's schema?** Same path — projections are deterministic functions of events, so a schema change is a code change, not a data migration.

CRUD systems handle these by writing migration scripts. Event-sourced systems handle them by replaying.

## 3. Time-travel comes free

Want to know the customer's address on 2023-12-31T23:59? Fold the events with `OccurredAt <= 2023-12-31T23:59`. No "history" table. No `AS OF SYSTEM TIME`. Just a different upper bound on the fold.

This is how Stratara's aggregation service handles read-side projections that need point-in-time views, regulatory snapshots, or "what did the system believe at the time we made this decision?" forensics.

## 4. The model captures *intent*

A CRUD update writes the new state. An event captures *what happened to produce that state*. `CustomerAddressChanged(newAddress, "customer-self-service-portal")` carries information that `UPDATE customer SET address=...` can never recover:

- That it was the customer themselves who changed it (not a CSR, not a script).
- That the change came from the self-service portal (not a phone call, not a back-office tool).
- The timestamp, the previous value, the correlation id of the request that caused it.

Domain analysis becomes possible. "How often do customers self-correct an address within 24h of order placement?" is a one-event-stream query — not a "we'd need to start logging that" project.

## 5. Concurrent business processes become tractable

Event streams version their state. Append fails fast if another process appended in between (`OptimisticConcurrencyException`). Sagas coordinate cross-aggregate workflows — `MoneyTransferInitiated` triggers `WithdrawCommand` on one account, success triggers `DepositCommand` on the other, failure triggers compensation. Each step is a recorded event, replayable in isolation.

Compare to CRUD coordination: distributed transactions, locking, "did the second call actually happen?" debugging through opaque last-write-wins semantics.

## The trade-offs (because there always are)

- **Read latency for fresh data is higher.** Projections lag behind the writes by a few milliseconds. If you need "the moment the event committed, the read API reflects it," you read from the write store, not the projection — Stratara supports that via `IAggregationService.AggregateAsync<TAggregate>(streamId)`.
- **Storage volume grows.** You're storing the history forever. Snapshots help (Stratara's snapshot store accelerates aggregate rebuild) but you don't get to discard the underlying events.
- **The team has to learn the model.** Append-only thinking, eventual consistency between write and read, replay-as-an-operations-tool. It's not hard, but it's different.

## Where it pays off

- **Auditable domains.** Anything regulated, anything where "what did the system believe at time X?" is a real question.
- **Multi-step business processes.** Onboarding, claims, transfers, fulfillment — anything that decomposes into a chain of events with branches.
- **Anywhere you'll want new reports later.** Replay means you don't have to commit upfront to every view the business will ever need.
- **Multi-tenant systems where integrity matters.** Combine with [Tamper-Evident Streams](tamper-evident-streams.md) and [Tenant-Aware Encryption](tenant-aware-encryption.md), and the storage layer is doing meaningful work for you.

## Where to start

- **[Stratara.Sample.EventSourced](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.EventSourced)** — the learning-path sample. Bank account, three events, a projection. ~250 lines, runs in a second.
- **[First Stratara App](../getting-started/first-stratara-app.md)** — the 30-line walkthrough wiring `IEventSource` + `IAggregationService` into your own host.
- **[Routing Conventions](../reference/routing-conventions.md)** — when to use the outbox versus the in-process mediator, once you start handling commands.
