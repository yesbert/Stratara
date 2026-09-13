# Close the gap between commit and publish

> **Status:** proposed

## Why

A save commits the events and only afterwards publishes their bundle; publishing tries the bus first
and writes to durable storage only if the bus refuses. Every unit of work opens its own context, so
the storage write, when it happens, is not part of the transaction that committed the events. A
process that dies between the commit and the publish leaves facts in the store that no projection and
no saga ever receives. `outbox-and-messaging` → *Delivery is at least once, never at most once* covers
a message that reached durable storage; a bundle that reached neither the bus nor storage is covered
by nothing.

Measured in change `prove-an-orleans-execution-model`, T1: a host ended between commit and publish
twenty times lost twenty bundles on the bus path. Recorded there as finding SF-002 and decided by the
owner on 2026-09-13: state the gap in the specification, and evaluate writing the outbox row in the
same transaction as the events.

## What Changes

- The specification says what a consumer of the bus path can rely on today: a bundle for committed
  events reaches the bus or durable storage unless the process ends between the commit and the
  publish, and what a consumer must do about it.
- A host can opt in to durable bundles: the save writes the bundle to durable storage in the
  transaction that commits the events, the publish becomes the fast path that removes it, and a
  committed fact is in durable storage until the bus has accepted it. The evaluation the owner asked
  for is in `design.md` → D1: worth shipping, as an opt-in, default unchanged in 4.x, because
  switching every host would be the silent change of semantics and of throughput the finding's change
  forbids. The cost is measured before the guide calls it cheap.

Additive API only: two default-implemented members on the bundle dispatcher port, one option, one
optional property on the bundle record. A host that changes nothing sees nothing; a host that opts in
sees one row written and removed per save.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `outbox-and-messaging`: *Delivery is at least once, never at most once* names the window on the
  default path and what closes it; *Dispatch attempts the bus first and falls back to durable
  storage* gains the opt-in for bundles.
- `event-sourcing-store`: *A successful save publishes what was written* says the bundle is durable
  with the commit on the opt-in, and that the save never fails after its commit.

## Impact

- `src/Stratara.Abstractions/Abstractions/Outbox/IEventBundleOutboxDispatcher.cs` — two
  default-implemented members.
- `src/Stratara.Infrastructure/EventSourcing/EventSource.cs` — the save stores the bundle under its
  own transaction when the dispatcher says so.
- `src/Stratara.Outbox.RabbitMQ/Outbox/EventBundleOutboxDispatcher.cs`, `OutboxOptions.cs` —
  store-publish-delete for bundles on the option.
- `src/Stratara.Contracts` — an optional storage id on the bundle record.
- `evidence/` in this change — the kill test and the cost measurement, recorded the way
  `prove-an-orleans-execution-model` records its runs.
- Superseded sources: none.
