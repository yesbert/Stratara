# Design — Lose no fact to a late subscription

## Context

See `proposal.md` → *Why*. What matters here is the shape of what exists.

`RabbitMqBus.SubscribeAsync` does four things in one call: declare the exchange, declare the queue,
bind it, attach the consumer. Establishing and consuming are welded together, so a subscription's
queue cannot exist before its handler does.

`PublishAsync` sets `mandatory=true` and surfaces a broker return as a failure, which sends the
message to durable storage. That is a real protection and it is why this is hard to see: a return
fires only when *no* queue is bound. One bound subscription confirms every publication on the topic.

The two subscriptions that collide in practice are the framework's own:

```
Stratara.Projections/Services/ProjectionWorker.cs:88   event-bundle-subscription
Stratara.Sagas/Services/SagaWorker.cs:85               event-bundle-saga-subscription
```

Both are `BackgroundService`. Both subscribe inside `ExecuteAsync`, which the base class schedules
*after* `StartAsync` returns. Both already override `StartAsync`, where they currently only log.

## Goals / Non-Goals

**Goals:**

- A subscription can exist before the first publication, independent of when its handler is ready.
- Whoever publishes can establish a subscription that belongs to a process which has not started.
- The abstraction expresses "established" separately from "consuming", so an implementation cannot
  claim the first while only doing the second.

**Non-Goals:**

- Retention semantics beyond what the broker already offers. This does not add replay, dead-lettering
  or a durable buffer in front of the bus.
- Changing what `SubscribeAsync` means for an existing caller.
- Calling the new member on a consumer's behalf, from the framework's own workers or anywhere else.
  See the decision below — it was planned, and reversed.

## Decisions

### The member is on the abstraction, and it breaks

`IMessageBus` gains `EnsureSubscriptionAsync(string topic, string subscription, CancellationToken)`.

*Rejected: a default interface implementation.* It would keep every external implementer compiling.
It would also do nothing until someone overrode it, so a host calling it would believe its
subscriptions were established when they were not — a subscription that looks established and is not
is exactly the defect being fixed. The breakage is the feature: the compiler names every implementer
who has to decide what establishing means for their transport.

*Rejected: an optional capability interface* (`ISupportsEarlySubscription`, tested with a cast). Same
silence in a different shape — a transport that does not implement it degrades quietly, and the
degradation is invisible at the call site.

### Why the framework does not call this for you

*Reversed during implementation, on the owner's challenge — the original plan had the projection and
saga workers establish their own subscriptions in `StartAsync`, and the reasoning did not survive
contact with the case it was meant to fix.*

The two workers do own the subscriptions that collide, and both already override `StartAsync`, so the
hook was there. But moving the establishing a few seconds earlier **inside each process** does not
synchronise anything **between** processes. In the incident that motivated this change the two
workers were separate deployments starting twenty-three seconds apart, both waiting on a shared
database migration lock. Establishing in `StartAsync` would have run after that wait, not before it,
and the publication would still have landed in the gap.

So the framework-side call would have helped a single host running both workers, done nothing for
separate ones, and — worst of all — been described in the changelog as protection a consumer gets for
free. A guarantee that holds in one topology and silently does not hold in another is the failure
class this whole change exists to remove.

What actually closes the gap is the property the new member has and `SubscribeAsync` never had:
**anyone can establish anyone's subscription.** The process that publishes first establishes every
subscription it is about to publish to, and from then on the queues are durable and it no longer
matters when — or whether — a worker has started. That is two lines in the publishing host, and it is
a decision about deployment order, which the framework is not in a position to know.

*Consequence, and it is a real one:* messages now accumulate in a queue whose worker never starts,
where before they were dropped. That is the correct direction — a fact kept is better than a fact
lost — but it is an operational quantity that did not exist before, and the guide says so.

### Client subscriptions are refused, not silently accepted

RabbitMQ client subscriptions (`default-*`) are exclusive and auto-deleting: their queue dies with
the channel that declared it. Establishing one early would create a queue that vanishes before the
handler attaches — worse than not establishing, because it looks like it worked.

The transport therefore refuses early establishment for them rather than accepting it as a no-op. A
no-op would be indistinguishable from success, which is this change's whole subject.

### The test double retains rather than records

`InMemoryMessageBus` records every publication for assertion. Recording is not retention: nothing is
delivered to a handler that attaches later. Once the real broker keeps messages for an established
subscription, a double that drops them makes a start-up-ordering test pass where production fails.

The double therefore holds messages published after a subscription is established and delivers them
on attach, in order.

## Risks / Trade-offs

**A published interface gains a member and every external implementer breaks.** → It is deliberate,
and it is confined to the `4.0.0` window that is open now. The alternative shapes all trade the
breakage for silence, which is the defect. The changelog entry names the member and the one-line
implementation a no-op transport needs.

**A queue whose worker never starts now grows instead of staying empty.** → Before, a message with no
bound queue was dropped; now an established subscription holds it. Keeping a fact is the right
direction and it is the point of the change, but it is an operational quantity that did not exist
before. A consumer that establishes a subscription for a worker it then never deploys will fill a
queue. The guide says so where the two lines are shown.

**A host calls the new member and the broker is unreachable.** → It throws, at the point the host
chose to call it, which is a caller's decision to handle rather than the framework's to absorb. The
framework does not call it anywhere, so no start-up path gains a broker round trip it did not have.

**The in-memory double could grow unbounded in a long test.** → Retention starts when a subscription
is established and holds until a handler attaches, which in a test is the span of one arrangement.
Nothing retains for a subscription that was never established, which is the existing behaviour.

**A green integration test proves nothing if it uses one subscription.** → Two tests carry this, both
with two subscriptions on one topic. The first pins today's behaviour — B binds late, receives
nothing, and the publish raises nothing — and it passes before the change, because it asserts the
defect. The second is the one that could not exist before: establish B, publish, attach, receive.
Neither is a red-then-green pair; the first is a characterisation test and says so.

## Migration Plan

1. Pin the defect with an integration test that asserts today's behaviour, including its silence.
2. Add the member to the abstraction and implement it in both transports and the test double.
3. Fix the implementers the compiler names.
4. Prove it with the counterpart test: establish, publish, attach, receive.
5. Document how a consumer uses it, because nothing uses it automatically.

*Rollback:* revert. Nothing persists, no message changes shape, and no stored state is written that a
previous version could not read.
