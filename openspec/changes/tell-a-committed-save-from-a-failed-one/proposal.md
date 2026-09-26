# Tell a committed save from a failed one

> **Status:** approved

## Why

`SaveChangesAsync` commits the staged events and then hands their bundle to the outbox. Two cases around
that handover behave in ways a caller cannot work with.

- **The handover fails after the commit.** On a host without durable bundles, the outbox tries the bus
  and then its own table, in a transaction of its own. When both fail, the save throws what the outbox
  threw, although the events are already committed. The caller cannot tell this from a save that wrote
  nothing. Retrying the command, which a pipeline that retries on any exception does, records the same
  facts a second time. Meanwhile the bundle is lost: readers that consume bundles never see the events
  until they are republished or replayed.
- **A save with nothing staged still runs.** It opens a transaction, writes an empty bundle on a host
  with durable bundles, and publishes it. That costs a round trip and a message for nothing. Since a
  failed save discards its batch, a caller retrying the save without appending again now hits exactly
  this path.

## What Changes

The consumer-visible effect: a save that fails after its events were committed says so, with a failure
of its own that the framework's retrying pipelines do not retry, and a save with nothing staged does
nothing.

- New `CommittedEventsNotPublishedException` in `Stratara.Abstractions.EventSourcing`. It carries the
  committed streams and the number of events, and its message says the events are durable and must not
  be appended again. `SaveChangesAsync` throws it when handing the bundle on fails after the commit,
  a cancellation included.
- Nothing in the framework runs the work again because of it:
  - the RabbitMQ and Azure Service Bus transports acknowledge the message instead of redelivering it,
    and log an error;
  - the Orleans execution model completes a recorded command instead of resuming it, and logs an
    error;
  - the pipelines that retry any failure (`ResilienceNames.CommandDispatcher`,
    `ResilienceNames.EventBundleDispatcher`, `ResilienceNames.MessageBus`,
    `ResilienceNames.ProjectionReplayBatch`) do not retry it.
- A save with nothing staged stores and publishes no bundle. It still requires a session, so a save
  without one fails as before.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `event-sourcing-store`: *A successful save publishes what was written* gains a save with nothing staged
  and a handover that fails, or is cancelled, after the commit.
- `outbox-and-messaging`: a new requirement — a message whose handler committed its events is not
  delivered again.
- `orleans-execution`: two new requirements — a recorded command whose events were committed is not
  run again, and a framework failure keeps its type between silos.

## Impact

- `src/Stratara.Abstractions/Abstractions/EventSourcing/CommittedEventsNotPublishedException.cs` (new).
- `src/Stratara.Abstractions/Abstractions/EventSourcing/IEventSource.cs` — the `SaveChangesAsync`
  documentation.
- `src/Stratara.Infrastructure/EventSourcing/EventSource.cs` — the empty save, and the wrap after the
  commit.
- `src/Stratara.Resilience/Resilience/ResilienceFactory.cs`, `ResilienceNames.cs`, `README.md` — the
  retry predicate of every retry-any pipeline.
- `src/Stratara.Outbox.RabbitMQ/Messaging/RabbitMqBus.cs`,
  `src/Stratara.Outbox.AzureServiceBus/Messaging/AzureServiceBusBus.cs` and their log extensions —
  acknowledge and log.
- `src/Stratara.Orleans/Aggregates/AggregateGrain.cs` (`CommandExecution.RunIntentAsync`) and
  `OrleansLog.cs` — complete and log.
- `src/Stratara.Diagnostics/LogEvents.cs` — `Messaging.CommittedEventsNotPublished` (`108_113`),
  `Orleans.IntentCommittedNotPublished` (`117_127`).
- `docs/guides/write-a-command-handler.md` and `docs/guides/use-resilience-policies.md` — what the
  failure means.
- `CHANGELOG.md`.
