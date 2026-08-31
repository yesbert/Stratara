> **Status:** approved

# Lose no fact to a late subscription

## Why

A subscription's queue exists only from the moment its worker subscribes, so anything published
before that is lost to it. With one subscription on a topic this is caught: the publication reaches
nobody, the broker returns it, the framework treats the return as a failure, and the message falls to
durable storage. With **two** subscriptions the first one bound is enough for every publication to be
confirmed, and the absence of the second is indistinguishable from success. The publisher is told the
publication worked, no row is written, nothing retries, nothing logs.

The projection worker and the saga worker share one topic. That is exactly the shape, and both of
them are the framework's own.

This is observed, not theorised. Two nightly runs of a consumer's end-to-end suite, same commit
range, opposite outcomes, decided by which worker won a shared database migration lock:

```
failing run                          passing run
───────────                          ───────────
12:32:38  saga binds                 13:09:52  saga binds
12:32:50  ◀── the fact is published  13:09:55  projection binds
12:33:01  projection binds           13:10:10  ◀── the fact is published
```

In the failing run the publication landed between the two. The saga's queue took it; the projection's
did not exist. The projection never saw the creating fact, answered every request for the resulting
view with 401 for the full five-minute wait, and logged 187 warnings of the shape *"event for
non-existent entry"* — creating facts lost, follow-ups delivered. The passing run logged none.

Beyond a test suite this is every cold start where one subscriber binds before another: a new
environment, a host rebuild, a restore after an outage.

**Why now:** the fix adds a member to the published `IMessageBus`, and the `4.0.0` cycle is open and
untagged. After the stable tag the same fix costs another major, or has to be deformed into something
non-breaking that does nothing until an implementer notices — which is this finding's own failure
class, one level up.

## What Changes

- **A subscription can be established before anything publishes.** `IMessageBus` gains
  `EnsureSubscriptionAsync(topic, subscription, cancellationToken)` — idempotent, and establishing
  only: it creates the durable subscription without dispatching anything.
- **`SubscribeAsync` keeps establishing the subscription itself**, so every existing caller stays
  correct unchanged. The new member exists so that establishing can happen *earlier* than a handler
  is ready, not instead of it.
- **BREAKING: `IMessageBus` gains a member.** Every implementation outside this repository must add
  it. Deliberate: a default interface implementation would keep the compile green and do nothing,
  which is a subscription that looks established and is not.
- **The RabbitMQ transport separates declare-and-bind from attaching a consumer**, and refuses early
  establishment for client subscriptions — they are exclusive and auto-deleting, so binding one early
  creates a queue that vanishes with the channel. Only durable worker subscriptions can meaningfully
  be established ahead of time, and they are the ones that lose facts.
- **The Azure Service Bus transport treats it as a no-op**, because its subscriptions are created
  administratively and already exist before anything publishes.
- **The in-memory test double retains and replays.** Once a subscription is established it holds what
  is published for it and delivers on attach — otherwise the double drops what the real broker now
  keeps, and a green test would prove the opposite of what it claims.

## Capabilities

### New Capabilities

None. This change introduces no capability.

### Modified Capabilities

- `outbox-and-messaging`: the capability guarantees delivery *"is at least once, never at most
  once"* — but that requirement opens with **"A message that reaches durable storage SHALL be
  retried"**, and the gap is upstream of durable storage. The message never reaches it, because a
  confirmed publication is not a failure. The guarantee is not violated; it never engages. Added: a
  subscription can be established before publication begins, and one that has been established loses
  nothing published after that point.
- `test-support`: the message-bus double promises *"every message is recorded for assertion whether
  or not anything was listening"*. Recording is not retention. The double must hold messages for an
  established subscription and deliver them when a handler attaches, so a test exercising start-up
  ordering fails for the same reason production would.

## Impact

**Changed**

`src/Stratara.Abstractions/Abstractions/Messaging/IMessageBus.cs` (the new member),
`src/Stratara.Outbox.RabbitMQ/Messaging/RabbitMqBus.cs` (declare-and-bind extracted and reused),
`src/Stratara.Outbox.AzureServiceBus/Messaging/AzureServiceBusBus.cs` (no-op),
`src/Stratara.Testing/InMemoryMessageBus.cs` (retain and replay).

**Must be updated or they will not compile**

The two sample `InMemoryMessageBus` classes under `samples/`, and `RecordingBus` in
`tests/Stratara.Outbox.RabbitMQ.Tests/Mediator/CommandWorkerLaneTests.cs`. That is the useful half of
a breaking change: the compiler names everyone who has to think about it.

**Consumer impact**

A consumer that only uses the framework recompiles nothing and behaves exactly as before: nothing
calls the new member on its behalf. What it gains is the ability to close the gap itself, in two
lines, from whichever process publishes first. A consumer that implements `IMessageBus` itself adds
one member.

**This change deliberately makes no promise about who calls it.** Establishing is a decision about a
deployment's start-up order, and the framework does not know one. See `design.md` → *Why the
framework does not call this for you*.

**A prior implementation exists and is not being applied**

A complete patch for the API half was written in a consumer's repository against that project's rule
that framework fixes are never made from a consumer session, and reverted there. It has been read as
a proposal and was verified to still apply cleanly, which is not a reason to apply it. It also stops
at the API: it leaves the calling to the consumer, so every other consumer would keep the bug. This
change is built test-first, and the test that decides it is the one with two subscriptions.

**Unaffected**

Every other capability, the outbox drain, the dispatch path, and the wire format. No message changes
shape.
