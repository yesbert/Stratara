# Design — Dead-letter what a handler cannot take

## Context

See `proposal.md` → *Why*. What follows is the state on `main` at `1476af9`, established by reading
the two transports on 2026-09-13 for finding SF-001 of `prove-an-orleans-execution-model`.

**RabbitMQ** (`src/Stratara.Outbox.RabbitMQ/Messaging/RabbitMqBus.cs`). `DeclareAndBindAsync`
(line 208) declares a fanout exchange per topic and one queue per subscription: durable classic
queues for worker subscriptions, exclusive auto-delete queues for the `default-` client
subscriptions (line 202). No queue argument is set. The consumer (line 231) acknowledges on
success, `BasicNack(requeue: true)` on `ConcurrencyException` (line 247) and
`BasicNack(requeue: false)` on anything else (line 252). Without a dead-letter exchange the second
is a drop. The comment at line 205 records the constraint that matters for any topology change: a
redeclaration with different arguments is refused by the broker.

**Azure Service Bus** (`src/Stratara.Outbox.AzureServiceBus/Messaging/AzureServiceBusBus.cs`).
`AbandonMessageAsync` on a conflict (line 96) — the broker redelivers until its own
`MaxDeliveryCount` and then dead-letters — and `DeadLetterMessageAsync` on anything else
(line 101), immediately, on the first failure.

So the two transports differ twice: a failure is retried zero times on both, but kept on one and
dropped on the other; a conflict is retried without bound on one and under the broker's count on
the other.

**The broker in the integration tests** is `rabbitmq:4-management-alpine`
(`tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Fixtures/RabbitMqFixture.cs:13`). RabbitMQ 4
supports quorum queues, the `x-delivery-limit` argument and the `x-delivery-count` header on
redelivery.

**Documentation that says otherwise.** `MediatorCommandWorker.cs:38` — "other errors
dead-lettered"; `BusEnvelopeIntegrityMode.cs:27` — "NACK-discard on RabbitMQ" (accurate today,
wrong after this change); `projections` → *A failing projection stops the bundle* — states the
discard; `docs/guides/outbox-setup-azureservicebus.md:80` — describes the broker's DLQ;
`docs/guides/outbox-setup-rabbitmq.md` — says nothing about failure.

## Goals / Non-Goals

**Goals:**

- The same message with the same failing handler ends in the same place on both transports: the
  subscription's dead-letter destination, after the same number of attempts.
- A conflict is bounded, so a permanently conflicting message cannot circulate forever.
- The framework decides when to dead-letter, from a count it reads on both transports; the broker's
  own limits are backstops, not the policy.
- An operator can find the destination by name from the subscription's name.

**Non-Goals:**

- A replay API. Returning a dead-lettered message is an operator action with the broker's tools
  (shovel or the management UI on RabbitMQ; the portal, the CLI or a peek-and-resubmit on Service
  Bus). A framework-side replay is a later change if operators ask for it.
- A delay between retries. A redelivery is immediate on both transports today and stays so; a
  back-off would need delayed-message infrastructure on RabbitMQ, which is its own topology.
- Dead-lettering for the exclusive client subscriptions (`default-` prefix). They are auto-delete
  queues for a process that is listening right now; a message nobody could take there has no
  operator to return it. They keep the current behaviour, and `BusEnvelopeIntegrityMode`'s remark
  says so.
- The outbox table. A stored message that cannot be published stays stored (`outbox-and-messaging`
  → *A worker drains durable storage in batches under a distributed lock*); that is already the
  right behaviour and is untouched.

## Decisions

### D1 — RabbitMQ worker queues become quorum queues with a dead-letter exchange

Worker subscriptions are declared with `x-queue-type: quorum`, `x-dead-letter-exchange: ""` (the
default exchange) and `x-dead-letter-routing-key: <subscription>.dead-letter`, which is a quorum
queue of that name; `x-dead-letter-strategy: at-least-once` with the `x-overflow: reject-publish`
it requires, so the move itself cannot lose the message. A `BasicNack(requeue: false)` on such a
queue routes the message to the dead-letter queue instead of dropping it. *Changed during apply,
2026-09-13:* the first draft used a per-topic direct exchange; because the dead-letter exchange is
part of what the broker compares on redeclaration, a subscription bound to two topics would have
failed its second declaration — the default exchange keeps the arguments a function of the
subscription alone and needs no extra exchange.

Quorum queues stamp a delivery count on every redelivery, which is the count the consumer reads to
apply the bounds (D3). *Found during apply, 2026-09-13:* RabbitMQ 4.3 no longer increments
`x-delivery-count` for a redelivery the consumer asked for with `basic.nack(requeue)` — only for one
it did not ask for, such as a channel that closed mid-message — and stamps the number of earlier
deliveries in a new header, `x-acquired-count`. Without that the bound never fired: a poison message
looped 6 944 times in five seconds. The consumer therefore reads `x-acquired-count` first and
`x-delivery-count` as the fallback for earlier versions (unverified live; the integration image is
4.3). Because 4.3 also preserves `x-acquired-count` on a dead-lettered message and a shovel copies
headers, a message an operator returns would arrive with its exhausted count: the consumer treats a
delivery the broker does not flag as redelivered as a first delivery, whatever headers it carries.
`x-delivery-limit` is set one above the larger bound as a backstop for the redeliveries RabbitMQ
4.3 does count — a consumer that dies mid-message — and to override the default limit of 20 that
4.x applies to quorum queues.

*Alternative considered:* classic queues with a dead-letter exchange and a re-publish carrying an
attempt header instead of a requeue. That keeps the queue type but means every retry is a publish
by the consumer — a second wire trip, a message whose identity changes on the broker, and an
ordering that differs from a requeue. Quorum queues give the count for free and the broker does the
requeue. The cost is D2.

*Alternative considered:* leave the queue type and only add the dead-letter exchange, keeping
conflicts unbounded. Fixes the drop, not the loop; the spec bounds both.

Evidence: RabbitMQ 4 documentation on quorum queues and dead lettering; the integration test in
task 3.1 shows the message on the dead-letter queue.

### D2 — The queue type changes under a new topology version, not in place

A durable classic queue named `<subscription>` already exists on every consumer's broker. Declaring
it again as a quorum queue fails with `PRECONDITION_FAILED` and closes the channel — the constraint
`RabbitMqBus.cs:205` records. Two ways out, and this change takes the second:

1. The operator deletes the worker queues before deploying. Simple, but a queue with messages in
   it loses them, and a rolling deployment has old and new consumers declaring different types.
2. The queue name carries a topology version: `<subscription>` becomes `<subscription>.v2` for
   quorum declarations. Old consumers keep their classic queue and drain it; new consumers bind the
   new one to the same fanout exchange; during the overlap every bundle is delivered to both and
   both sides apply it idempotently, as at-least-once already requires. When the old queue is empty
   the operator deletes it.

The suffix is a constant in the RabbitMQ transport, documented in
`docs/guides/outbox-setup-rabbitmq.md` with the two-step rollout. `EstablishSubscriptionAsync` and
`SubscribeAsync` share `DeclareAndBindAsync`, so both paths declare the same thing.

*Alternative considered:* detect an existing classic queue with a passive declare and keep using it
unchanged. That leaves the discard in place for every existing consumer indefinitely and makes the
guarantee depend on deployment history — exactly the kind of silent split the spec forbids.

Evidence: the constraint at `RabbitMqBus.cs:205`; RabbitMQ's documented refusal to change a
queue's type after creation.

### D3 — The framework applies the bounds from the delivery count; the broker's limits are backstops

On both transports the consumer reads the delivery count the broker supplies — the
`x-acquired-count` (4.x) or `x-delivery-count` (3.x) header on RabbitMQ (absent on the first
delivery, so 0), `DeliveryCount` on Service Bus (1 on the first) — and decides:

| Outcome | Count under the bound | Count at the bound |
|---|---|---|
| Handler succeeded | acknowledge / complete | acknowledge / complete |
| `ConcurrencyException` | requeue / abandon | dead-letter, reason `conflict` |
| Any other exception | requeue / abandon | dead-letter, reason `failure` |

Service Bus therefore changes on both rows: a failure is abandoned until the bound instead of
dead-lettered at once, and a conflict is dead-lettered by the framework at its bound instead of by
the broker at `MaxDeliveryCount`. The broker's `MaxDeliveryCount` must be at least the larger bound
for the policy to be the framework's; the Service Bus guide says so, and the transport logs a
warning at startup if the subscription's count is lower — the property is readable through the
administration client when the host has the rights, and the check is skipped when it does not.

*Alternative considered:* let each broker's native limit be the policy. Then the bound is
configured in two different places for two transports and nothing above the transport can name
it — the requirement that both transports behave alike would be a deployment accident.

Evidence: unit tests in task 2.3 for the decision table on each transport.

### D4 — Two bounds, one options type, in `Stratara.Abstractions`

```csharp
public sealed class MessageRetryOptions
{
    public int MaxDeliveryAttempts { get; set; } = 3;    // handler failures
    public int MaxConflictRequeues { get; set; } = 100;  // ConcurrencyException
}
```

The type sits next to `BusEnvelopeJsonOptions` in `Stratara.Abstractions.Messaging`, bound from
configuration by both transports' registrations under one section, so a host that switches broker
keeps its bounds.

The defaults are a judgement, recorded as one: three attempts distinguish a transient failure (the
second attempt passes) from a deterministic one (all three fail identically) without holding a
poisoned message long; a hundred conflict requeues are far above what the per-process
serialisation in the projection and saga workers lets through under normal contention, and far
below forever. Neither is measured; a consumer that sees the dead-letter counter rise on conflicts
should raise the second before assuming a bug.

### D5 — One log event and one counter, on both transports

`LogEvents.Messaging` gains `MessageDeadLettered` at warning level with topic, subscription, reason
and the delivery count. `ApplicationDiagnostics.Metrics` gains `MessagesDeadLettered`, a counter
dimensioned by topic, subscription and reason. Both are raised by the transport at the moment it
dead-letters — on Service Bus also when the framework's check fires, never for the broker's own
`MaxDeliveryCount` path, which the framework cannot observe.

### D6 — The documentation fixes do not wait for the code

`MediatorCommandWorker.cs:38` is wrong today and stays wrong until this change ships unless it is
fixed first; the PoC change proposed the one-file fix and this change carries it as task 1.1 so it
lands in the same pull request as the behaviour it describes. `BusEnvelopeIntegrityMode.cs:27`
changes meaning with the code and is fixed with it (task 4.1).

## Risks / Trade-offs

- **Quorum queues need a RabbitMQ cluster that supports them (3.8+; the integration tests run 4).**
  → A consumer on an older broker gets a channel error at declaration. The RabbitMQ guide names the
  minimum; the framework does not fall back to a classic queue silently.
- **The topology version leaves an orphan classic queue behind.** → Documented deletion step in the
  guide; the old queue is bound to the fanout exchange and would otherwise fill with every bundle
  and never drain once the old consumers are gone.
- **A message that fails because of its content (poison) still costs three handler runs.** →
  Accepted; the bound is configurable down to one.
- **Conflict requeues are immediate, so a hot aggregate burns its hundred quickly under a real
  storm.** → The counter shows it as `conflict` dead-lettering, which is the signal to raise the
  bound or to look at the contention; the message is not lost either way.
- **Service Bus's `DeliveryCount` starts at 1, RabbitMQ's `x-delivery-count` at absent.** → One
  normalisation in each transport, pinned by the unit tests in task 2.3; the bounds mean the same
  number of handler runs on both.

## Migration Plan

1. Ship the code with the new queue suffix and the new options with their defaults. A host that
   changes nothing gets bounded retries and a dead-letter queue per worker subscription on RabbitMQ,
   and the framework's bounds on Service Bus.
2. RabbitMQ operators: deploy; wait until the old `<subscription>` queues are empty; delete them.
   Service Bus operators: confirm `MaxDeliveryCount` on the worker subscriptions is at least
   `MaxConflictRequeues`.
3. Rollback: the previous version declares the old queue names and finds them still there (if step
   2's deletion has not happened); messages on the `.v2` queues stay until an operator moves them.

## Open Questions

None that change the specs or the tasks. Whether operators want a framework-side replay command is
answered by them after this ships.
