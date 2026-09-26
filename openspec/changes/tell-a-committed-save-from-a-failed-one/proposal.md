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
  be appended again. `SaveChangesAsync` throws it when handing the bundle on fails after the commit.
  Cancellation still propagates as cancellation.
- The dispatcher retry pipelines (`ResilienceNames.CommandDispatcher`,
  `ResilienceNames.EventBundleDispatcher`) do not retry it.
- A save with nothing staged stores and publishes no bundle. It still requires a session, so a save
  without one fails as before.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `event-sourcing-store`: *A successful save publishes what was written* gains a save with nothing staged
  and a handover that fails after the commit.

## Impact

- `src/Stratara.Abstractions/Abstractions/EventSourcing/CommittedEventsNotPublishedException.cs` (new).
- `src/Stratara.Abstractions/Abstractions/EventSourcing/IEventSource.cs` — the `SaveChangesAsync`
  documentation.
- `src/Stratara.Infrastructure/EventSourcing/EventSource.cs` — the empty save, and the wrap after the
  commit.
- `src/Stratara.Resilience/Resilience/ResilienceFactory.cs` — the dispatcher retry predicate.
- `docs/guides/write-a-command-handler.md` and `docs/guides/use-resilience-policies.md` — what the
  failure means.
- `CHANGELOG.md`.
