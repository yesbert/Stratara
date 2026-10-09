## Context

`RabbitMqBus.EnsureSubscriptionAsync` (`src/Stratara.Outbox.RabbitMQ/Messaging/RabbitMqBus.cs`) opens a
throwaway connection and calls `DeclareAndBindAsync`, which declares the dead-letter queue, then the worker
queue `<sub>.v2` on a channel of its own (`DeclareWorkerQueueAsync`; a refusal because of a different
`x-delivery-limit` is tolerated and confirmed with `QueueDeclarePassiveAsync`), then binds it. Both
`QueueDeclareAsync` and `QueueDeclarePassiveAsync` return a `QueueDeclareOk` carrying `MessageCount` and
`ConsumerCount`; the code discards it. The worker queue is a quorum queue with
`x-overflow: reject-publish` and at-least-once dead-lettering; no length, byte or TTL argument.

Nothing in `src/` calls `EnsureSubscriptionAsync`; consumers call it at start-up from the hosts that publish
(the guide's pattern; NextPA's command and outbox workers). The Azure Service Bus implementation makes it a
no-op on purpose (data-plane credentials). `MessagingOptions` (section `Messaging`, `Stratara.Shared`) is
validated in `AddMessaging` (`PrefetchCount` 1..65535).

Evidence: the implementation at `main` 398d20d; the 4.0.0 changelog entry (*What it costs*) and the archived
design `2026-08-31-lose-no-fact-to-a-late-subscription/design.md` lines 111–115, whose only mitigation was
"the guide says so"; NextPA finding F-010 (*What it costs*).

## Goals / Non-Goals

**Goals:**
- An orphaned subscription is named in a log at the next start of a host that establishes it.
- No extra broker round trip, no background loop, no change to any queue's arguments.

**Non-Goals:**
- Capping or expiring the queue. `x-max-length` with the queue's `reject-publish` overflow makes the broker
  nack publications to the full queue; on a fanout topic the publication then falls back to the outbox, is
  retried, and is delivered again to every other subscription each time — a backlog in one orphan turns
  into duplicates everywhere else. Switching the overflow to `drop-head` discards, which the spec forbids,
  and would require redeclaring every existing queue with different arguments (`406 PRECONDITION_FAILED`).
  `x-message-ttl` dead-letters at-least-once into `<sub>.dead-letter`, which is unbounded too. A broker
  policy can do either without redeclaration, and that is the operator's choice, not the framework's.
- A periodic check or a metric. A gauge would need a passive declaration per subscription per interval from
  some host, and the framework does not know which host is the right one; the broker's management plugin
  already exports queue depth and consumer count.
- Azure Service Bus. Its subscriptions are provisioned administratively; Azure Monitor reports the active
  message count.

## Decisions

**Report at establishment, from the declaration's own answer.** `DeclareWorkerQueueAsync` returns the
`QueueDeclareOk` from whichever path ran (the declaration or the passive confirmation); `DeclareAndBindAsync`
hands it back; `EnsureSubscriptionAsync` compares it with the threshold. `ConsumerCount == 0 &&
MessageCount >= threshold` logs. The counts are read before the binding, which is fine: they describe what
the queue held before this host touched it.
- *Alternative:* report on `SubscribeAsync` too. Rejected: at that moment the host's own consumer is not yet
  attached, so the count is always 0, and a backlog is exactly what a restarted worker is expected to find.

**Threshold in `MessagingOptions`, default 10 000.** `UnconsumedSubscriptionWarningThreshold` (`int`). Ten
thousand is large enough that a worker restarting during a busy minute does not trip it, and small against
the point where a quorum queue starts to hurt a broker. Zero disables; a negative value is refused at start
by the existing `AddMessaging` validation, with a message naming `Messaging:UnconsumedSubscriptionWarningThreshold`.

**Log id `108_114`, Warning.** The next free number in the messaging band's `1xx` range (highest used
`108_113`). Message: "Subscription {Subscription} on topic {Topic} holds {MessageCount} messages and no
consumer is attached. An established subscription keeps what is published to it until something consumes
it; if its worker is retired, delete the subscription."

## Risks / Trade-offs

- [A worker deployed after its publishers, during a planned rollout, trips the warning once] → The message
  says what it means; the threshold makes it rare. Accepted.
- [A consumer that never calls `EnsureSubscriptionAsync` gets no report] → Then it never had the growth
  either: its subscriptions exist only while their handlers are attached, as before 4.0.0.

## Migration Plan

A version bump. Nothing to configure unless the default is wrong for the deployment.
