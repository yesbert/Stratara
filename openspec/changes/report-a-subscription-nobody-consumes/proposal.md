# Report a subscription nobody consumes

> **Status:** approved

## Why

Since 4.0.0 a host can establish a subscription before its handler attaches
(`IMessageBus.EnsureSubscriptionAsync`), which closed NextPA finding F-010 — facts lost to a subscription
that bound late. The change named its own cost and left it to the guide: an established subscription keeps
what is published to it until something consumes it, so a queue whose worker is never deployed, or was
retired, grows without end. Nothing in the framework notices. The first sign is a broker running out of
disk, and by then the queue holds weeks of messages for a consumer that no longer exists.

Bounding the queue is not the answer here: the framework promises that a message on a durable subscription
is never discarded (*A message a handler cannot take is retried a bounded number of times and then kept*),
a length cap with the queue's overflow mode refuses publications — which on a shared topic stalls every
other subscription through the outbox's retry — and a time-to-live only moves the growth to the
dead-letter queue. What is missing is that someone is told.

## What Changes

The consumer-visible effect: establishing a subscription that already holds a backlog and has no consumer
logs a warning that names it, so an orphaned subscription shows up in the publisher's log at its next start
rather than on the broker's disk alarm.

- When `EnsureSubscriptionAsync` finds that the subscription holds at least
  `MessagingOptions.UnconsumedSubscriptionWarningThreshold` messages (default 10 000; 0 turns it off) and no
  consumer is attached, it logs `108_114` at Warning with the topic, the subscription and the count.
- RabbitMQ only, where the declaration reports both numbers at no extra cost. On Azure Service Bus
  subscriptions are provisioned administratively and establishing one does nothing; the broker's own
  metrics cover it, and the guide says so.

Not changed: nothing is capped, expired or discarded; `SubscribeAsync`, which attaches the consumer, reports
nothing (a backlog there is a worker catching up); the queue arguments, so no existing queue is redeclared
differently.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `outbox-and-messaging`: a new requirement, *An established subscription that nobody consumes is
  reported*.

## Impact

- `src/Stratara.Outbox.RabbitMQ/Messaging/RabbitMqBus.cs` — `EnsureSubscriptionAsync` reads the message and
  consumer counts its declaration returns (both the normal and the tolerated-limit passive path) and logs.
- `src/Stratara.Shared/Messaging/MessagingOptions.cs` — `UnconsumedSubscriptionWarningThreshold`;
  `src/Stratara.Outbox.RabbitMQ/DependencyInjection/MessagingServiceCollectionExtensions.cs` — its validation
  (zero or more).
- `src/Stratara.Diagnostics/LogEvents.cs`, `src/Stratara.Outbox.RabbitMQ/Diagnostics/Extensions/LoggerMessagingExtensions.cs`
  — `108_114`.
- `docs/guides/outbox-setup-rabbitmq.md` (the misplaced *What it costs* paragraph moves under *Establish
  subscriptions before the first publish* and gains the warning), `docs/guides/outbox-setup-azureservicebus.md`
  (*Routing model*: the broker's metrics), `CHANGELOG.md`.
- No package, dependency or tier changes. Additive: a patch release.
