# Dead-letter what a handler cannot take

> **Status:** proposed

## Why

On RabbitMQ a message whose handler throws anything but a concurrency conflict is rejected without
requeue, and the worker queues are declared without a dead-letter destination, so the broker drops it.
A concurrency conflict is requeued without bound. On Azure Service Bus the same failure is
dead-lettered and the same conflict is abandoned under the broker's delivery limit. The same handler
failure is therefore recoverable on one transport and lost on the other, although the framework
promises that nothing above the transport depends on which broker is in use. The `MediatorCommandWorker`
documentation says a failing command is "dead-lettered", which on RabbitMQ it is not, and consumers
have planned for a dead-letter queue that does not exist.

Recorded as finding SF-001 of change `prove-an-orleans-execution-model` and decided by the owner on
2026-09-13: a bounded retry followed by a destination an operator can inspect and replay, on both
transports.

## What Changes

- A message a handler cannot take is retried a bounded number of times and then moved to a
  dead-letter destination the operator can inspect and replay, on every transport.
- A concurrency conflict is requeued a bounded number of times, not forever.
- The bounds are configurable with defaults, and the outcome of a dead-lettering is recorded and
  measured.
- The `MediatorCommandWorker` remark that says "dead-lettered" becomes true, and the
  `BusEnvelopeIntegrityMode` remark that says "NACK-discard on RabbitMQ" is corrected.
- `projections` no longer says the bundle is discarded on RabbitMQ; it says what happens instead.

No API is removed; no consumer code changes. A host that never configures the bounds gets the
defaults.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `outbox-and-messaging`: a new requirement on retry bounds and the dead-letter destination; the
  transport-replaceability requirement gains the scenario that both transports behave alike.
- `projections`: *A failing projection stops the bundle* stops describing a discard.
- `sagas`: a new requirement that a failing saga does not lose the bundle.

## Impact

- `src/Stratara.Outbox.RabbitMQ/Messaging/RabbitMqBus.cs` — queue declaration with dead-letter
  arguments, bounded requeue (quorum queues with a delivery limit, or a re-publish with an attempt
  header — decided in `design.md`).
- `src/Stratara.Outbox.AzureServiceBus/Messaging/AzureServiceBusBus.cs` — the bound on abandon.
- `src/Stratara.Outbox.RabbitMQ/Mediator/MediatorCommandWorker.cs` line 38 and
  `src/Stratara.Abstractions/Messaging/BusEnvelopeIntegrityMode.cs` line 27 — documentation.
- New options for the bounds; new log events and a metric for dead-lettering.
- Superseded sources: none.
