Ordered so that the documentation lie is gone first, the options and the transports follow, and
the tests that prove the spec scenarios come before the guides that describe them.

## 1. The wrong remark

- [x] 1.1 `src/Stratara.Outbox.RabbitMQ/Mediator/MediatorCommandWorker.cs:38`: the remark describes
      what the transport does after this change — a bounded redelivery, then the subscription's
      dead-letter destination, on both brokers — and no longer says "dead-lettered" as if it were
      true today on RabbitMQ. Verify: the paragraph names both bounds by their option names.

## 2. The bounds and the decision table

- [x] 2.1 `src/Stratara.Abstractions/Messaging/MessageRetryOptions.cs` (new): `MaxDeliveryAttempts`
      (default 3) and `MaxConflictRequeues` (default 100), XML docs in house style, validated at
      startup to be ≥ 1. Verify: `tests/Stratara.Abstractions.Tests` (or the nearest options test
      slice) proves the defaults and the rejection of 0.
- [x] 2.2 Bind the options in both registrations —
      `src/Stratara.Outbox.RabbitMQ/DependencyInjection/*ServiceCollectionExtensions.cs` and
      `src/Stratara.Outbox.AzureServiceBus/DependencyInjection/AzureServiceBusServiceCollectionExtensions.cs`
      — from one configuration section, so switching the broker keeps the bounds. Verify: a
      resolution test in `tests/Stratara.Outbox.RabbitMQ.Tests` shows a configured value on the
      resolved options.
- [x] 2.3 The decision table (design D3) as a small internal type shared by both transports:
      `(count, outcome) → Acknowledge | Redeliver | DeadLetter(reason)`. Unit tests in
      `tests/Stratara.Outbox.RabbitMQ.Tests` cover every row and both count normalisations
      (RabbitMQ absent-means-0, Service Bus starts at 1). Verify: the tests name the row they pin.
      *Done 2026-09-13: an internal type cannot be shared by two packages that do not reference each
      other, so the table is `MessageRetryPolicy`, public in `Stratara.Abstractions.Messaging` next
      to the options; the normalisations stay in each transport
      (`tests/Stratara.Outbox.RabbitMQ.Tests/Messaging/MessageRetryPolicyTests.cs`).*

## 3. RabbitMQ

- [x] 3.1 `RabbitMqBus.DeclareAndBindAsync`: worker subscriptions are declared as quorum queues
      under `<subscription>.v2` with `x-dead-letter-exchange`, `x-dead-letter-routing-key` and
      `x-delivery-limit` (design D1, D2); the dead-letter exchange and `<subscription>.dead-letter`
      are declared alongside; client subscriptions unchanged. Verify: integration test in
      `tests/Stratara.Outbox.RabbitMQ.IntegrationTests` declares, publishes to a handler that always
      throws, and finds the message on `<subscription>.dead-letter` after exactly
      `MaxDeliveryAttempts` handler runs.
- [x] 3.2 The consumer reads `x-delivery-count` and applies the table: `BasicNack(requeue: true)`
      under the bound, `BasicNack(requeue: false)` at it, for conflicts and failures separately.
      Verify: integration tests for *A handler fails once* (acknowledged, not dead-lettered) and
      *A handler keeps conflicting* (dead-lettered with reason `conflict` after
      `MaxConflictRequeues`). *Done 2026-09-13: RabbitMQ 4 stamps `x-acquired-count` and no longer
      increments `x-delivery-count` for a requested requeue (design D1, found during apply); the
      consumer reads the former first, the latter as the 3.x fallback.
      `tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Messaging/RabbitMqDeadLetterTests.cs`, 4/4.*
- [x] 3.3 An operator return works: the integration test shovels the message back (a plain
      publish to the queue in the test) and sees it delivered with the count starting over. Verify:
      the test asserts a fresh handler run.

## 4. Azure Service Bus

- [x] 4.1 `AzureServiceBusBus`: abandon on any failure under the bound, `DeadLetterMessageAsync`
      with the reason at it, from `DeliveryCount` (design D3); the startup warning when the
      subscription's `MaxDeliveryCount` is below the larger bound, skipped without administration
      rights. `src/Stratara.Abstractions/Messaging/BusEnvelopeIntegrityMode.cs:27`: the remark says
      "the transport's bounded redelivery and dead-letter policy" on both brokers and names the
      client-subscription exception. Verify: unit tests with
      `ServiceBusModelFactory.ServiceBusReceivedMessage(deliveryCount: …)` and a
      `ProcessMessageEventArgs` over a recording receiver, one per table row, in a new
      `tests/Stratara.Outbox.AzureServiceBus.Tests` project added to `Stratara.slnx` (not to the
      publish filter). *Done 2026-09-13, verified more strongly than planned: the repository already
      runs the Service Bus emulator as a Testcontainer
      (`tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Messaging/ServiceBusTests.cs`), so the two
      scenarios run against it — keeps-failing dead-lettered after 3 with reason `failure`,
      keeps-conflicting after `MaxConflictRequeues` with reason `conflict` — and no unit-test
      project was added. The spec scenario names the emulator.*

## 5. Log event and counter

- [x] 5.1 `src/Stratara.Diagnostics/LogEvents.cs` → `Messaging`: `MessageDeadLettered`;
      `ApplicationDiagnostics.Metrics.MessagesDeadLettered` dimensioned by topic, subscription and
      reason (design D5); raised by both transports at the moment they dead-letter. Verify: the
      integration test in 3.1 observes the counter through a `MeterListener`; the log-event schema
      test in `tests/Stratara.Diagnostics.Tests` (if present) accepts the new id.

## 6. Specs, guides and changelog

- [x] 6.1 `docs/guides/outbox-setup-rabbitmq.md`: the queue type, the minimum broker version, the
      `.v2` suffix with the two-step rollout and deletion of the old queue, where the dead-letter
      queue is and how to return a message. `docs/guides/outbox-setup-azureservicebus.md:80-82`:
      the framework's bounds now decide, `MaxDeliveryCount` must be at least the larger one, the
      DLQ reason values. Verify: both guides name `MaxDeliveryAttempts` and `MaxConflictRequeues`.
- [x] 6.2 Regenerate `llms-full.txt` with the generator the documentation tests use
      (`tests/Stratara.Documentation.Tests`), because the XML remarks changed. Verify: the
      documentation tests pass.
- [x] 6.3 `CHANGELOG.md` `[Unreleased]` → *Changed*: bounded redelivery and a dead-letter queue per
      worker subscription on RabbitMQ (new queue names, rollout step), bounded conflict retries on
      both brokers, the two options; → *Fixed*: the worker remark. Verify: the rollout step is in
      the entry, not only in the guide.

## 7. Gate

- [x] 7.1 `./scripts/local-gauntlet.sh` green; `dotnet test tests/Stratara.Outbox.RabbitMQ.IntegrationTests`
      green with Docker; `openspec validate dead-letter-what-a-handler-cannot-take --strict` clean.
