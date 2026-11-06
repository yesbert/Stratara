# Republish stored messages once per pass

> **Status:** approved (owner, 2026-11-06)

## Why

The consumer-visible effect today: on a deployment with several outbox workers, messages that wait in
durable storage are published more than once while other messages wait behind them. Republishing a
stored message goes through the same bounded retries as a first dispatch, about 1.4 seconds for every
message the bus refuses. About 43 such messages, for example heavy commands with no heavy worker bound,
are enough for a pass to outlive the default lock lease of 60 seconds. Another replica then takes the
lock and republishes the same rows, and the event bundles of the pass have not had their turn yet.

Those retries serve nothing here. The message is already safe in storage, and the next pass is its
retry.

## What Changes

- **A pass makes one attempt per stored message.** A stored message the bus refuses is left stored and
  attempted once on the next pass, for commands and for event bundles alike.
- **The first dispatch keeps its retries.** A dispatched command or bundle, durable bundles included,
  still goes through the bounded dispatch policy before it falls back to storage: there the retry is
  what avoids a database write.
- **No time budget, and no stopping early.** A pass still attempts every message of its batch. One that
  fails says nothing about the next, which may be for another topic.
- **Not breaking.** No public signature, option or log event changes.
- **What a consumer can notice:** a pass over unpublishable messages ends sooner and logs one failure
  per message per pass, and a publication that fails only for a moment waits for the next polling
  interval rather than for a retry 200 milliseconds later. A policy a host re-registers under a dispatch
  name no longer applies to republication from storage.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `outbox-and-messaging`: a drain pass attempts a stored message once.
- `resilience`: the dispatch policies govern a first dispatch and not republication from storage.

## Impact

- **Code:** `src/Stratara.Outbox.RabbitMQ/Outbox/CommandOutboxDispatcher.cs`,
  `src/Stratara.Outbox.RabbitMQ/Outbox/EventBundleOutboxDispatcher.cs`.
- **XML documentation:** `src/Stratara.Outbox.RabbitMQ/Outbox/OutboxOptions.cs` (`LockLeaseSeconds`),
  `src/Stratara.Resilience/Resilience/ResilienceNames.cs`.
- **Documentation:** `docs/guides/outbox-setup-rabbitmq.md`, `src/Stratara.Resilience/README.md`.
- **Transports:** the dispatchers sit above `IMessageBus`, so the change holds on either transport.
- **Data:** none.
- **Dissolved or superseded sources:** none.
