## 1. See it fail first

- [x] 1.1 Write the integration test that decides this change, in
      `tests/Stratara.Outbox.RabbitMQ.IntegrationTests` against the existing `RabbitMqFixture`:
      subscribe subscription A, publish, then subscribe subscription B. Verify: B receives nothing,
      **and** the publish raised no error — both halves matter, because the silence is the defect.
      A single-subscription variant of the same test passes today and proves nothing.
- [x] 1.2 Write its counterpart, which must fail until this change lands: establish B, publish,
      then attach B's handler. Verify: it fails now with B receiving nothing, and the failure names
      the missing member rather than a timeout.

## 2. The abstraction

- [x] 2.1 Add `EnsureSubscriptionAsync(string topic, string subscription, CancellationToken)` to
      `IMessageBus`, documented as idempotent, establishing-only, and as something `SubscribeAsync`
      also does — so a caller that only subscribes stays correct. Verify: the XML documentation says
      what a transport must do when it cannot establish early, and `dotnet build` fails for every
      implementer that has not been updated yet, naming each one.

## 3. The transports and the double

- [x] 3.1 `RabbitMqBus`: extract declare-and-bind from `SubscribeAsync` and reuse it for both paths,
      so establishing and subscribing cannot drift apart. Verify: subscribing after establishing
      binds no second queue and loses nothing, exercised by task 1.2's test.
- [x] 3.2 `RabbitMqBus`: refuse early establishment for client subscriptions (`default-*`) rather
      than accepting it as a no-op — their queue is exclusive and auto-deleting, so it would vanish
      before a handler attached. Verify: a unit test asserts the refusal and its message says why.
- [x] 3.3 `AzureServiceBusBus`: implement as a no-op, documented with the reason — its subscriptions
      are created administratively and exist before anything publishes. Verify: the XML comment
      states that, so the next reader does not "fix" it.
- [x] 3.4 `Stratara.Testing.InMemoryMessageBus`: retain what is published for an established
      subscription and deliver it in order when a handler attaches. Verify: a test in
      `Stratara.Testing.Tests` covers establish → publish → attach, and the existing recording
      behaviour for a never-established subscription is unchanged.
- [x] 3.5 Update the implementers the compiler names: the two sample `InMemoryMessageBus` classes
      and `RecordingBus` in `CommandWorkerLaneTests`. Verify: `dotnet build Stratara.slnx -c Release`
      is clean.

## 4. Tell a consumer how to use it

*Nothing calls the new member automatically, so the documentation is not an afterthought here — it is
the delivery mechanism. See `design.md` → "Why the framework does not call this for you".*

- [x] 4.1 Document the two lines in both outbox guides: establish every subscription that will be
      published to, from whichever process publishes first, before the first publication. Verify:
      each guide shows the call with names taken from `IMessagingIdentifier` rather than typed
      strings, and says the queues are durable so it only matters on a broker that has never seen
      them.
- [x] 4.2 Say what it costs: an established subscription whose worker never starts accumulates
      instead of dropping. Verify: the guides state it as an operational consequence, not a warning
      against using the call.
- [x] 4.3 Update the AI index so an assistant answering "how do I stop losing events on a cold
      start" reaches the member rather than inventing one. Verify: `llms.txt` names
      `EnsureSubscriptionAsync` in its messaging facts, and the generated `llms-full.txt` is
      regenerated rather than hand-edited.

## 5. Close it out

- [x] 5.1 Write the changelog entry under `## [4.0.0] — unreleased`, in the breaking section: the
      new member, the one-line no-op a transport needs, and what a consumer who implements nothing
      gets for free. Verify: it names the member and the two workers, and says the fix is silent
      for a consumer that only uses the framework.
- [x] 5.2 Run the gate and the container suite, which the unit tests do not cover. Verify:
      `./scripts/local-gauntlet.sh` green, and
      `dotnet test tests/Stratara.Outbox.RabbitMQ.IntegrationTests` green with Docker running —
      task 1.1's and 1.2's tests are in it.
