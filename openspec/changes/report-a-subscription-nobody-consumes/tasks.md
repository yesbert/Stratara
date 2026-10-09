## 1. Reproduce first

- [x] 1.1 `tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Messaging/RabbitMqBusTests.cs` (template:
  `EnsureSubscriptionAsync_EstablishedBeforePublish_DeliversOnceTheHandlerAttaches`, line 101), red until the
  report exists: `EnsureSubscriptionAsync_OverABacklogWithNoConsumer_Logs108114WithTheCount` (threshold
  lowered to 5 through options, six messages published to an established subscription, established again).

## 2. The option

- [x] 2.1 `MessagingOptions.UnconsumedSubscriptionWarningThreshold` (`int`, default 10 000), documented
  (`src/Stratara.Shared/Messaging/MessagingOptions.cs`).
- [x] 2.2 `AddMessaging` refuses a negative value at start
  (`src/Stratara.Outbox.RabbitMQ/DependencyInjection/MessagingServiceCollectionExtensions.cs`) —
  `tests/Stratara.Outbox.RabbitMQ.Tests/DependencyInjection/MessagingServiceCollectionExtensionsTests.cs`:
  `AddMessaging_RefusesANegativeUnconsumedSubscriptionWarningThresholdAtStart`,
  `AddMessaging_ReadsTheUnconsumedSubscriptionWarningThreshold` (the existing `MessagingOptions` binding case
  already proves the section is read).

## 3. The report

- [x] 3.1 `LogEvents.Messaging.UnconsumedSubscription = 108_114` and the source-generated method in
  `LoggerMessagingExtensions`.
- [x] 3.2 `RabbitMqBus`: `DeclareWorkerQueueAsync` returns the `QueueDeclareOk` from either path,
  `DeclareAndBindAsync` hands it back, `EnsureSubscriptionAsync` logs — the fact of 1.1 goes green; same file:
  `EnsureSubscriptionAsync_WithAConsumerAttached_ReportsNothing`,
  `EnsureSubscriptionAsync_BelowTheThreshold_ReportsNothing`,
  `EnsureSubscriptionAsync_WithTheThresholdAtZero_ReportsNothing`,
  `SubscribeAsync_OverABacklog_ReportsNothingAndDeliversIt`;
  `tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Messaging/RabbitMqDeadLetterTests.cs`
  `BoundsChangeOnAnExistingSubscription_…` (line 565) still green — the tolerated-limit path reports too.

## 4. Documentation

- [x] 4.1 `docs/guides/outbox-setup-rabbitmq.md`: move *What it costs* (lines 157–160) under *Establish
  subscriptions before the first publish* (lines 93–134), add the warning, the option and why the framework
  does not cap the queue (broker policy as the operator's tool).
- [x] 4.2 `docs/guides/outbox-setup-azureservicebus.md`, *Routing model* (around line 56): a backlog on a
  subscription nobody consumes shows in Azure Monitor's active message count; the framework reports nothing.
- [x] 4.3 `CHANGELOG.md` → *Unreleased*.

## 5. Verify

- [ ] 5.1 `openspec validate report-a-subscription-nobody-consumes --strict`.
- [ ] 5.2 `./scripts/local-gauntlet.sh` green.
- [ ] 5.3 `dotnet test tests/Stratara.Outbox.RabbitMQ.IntegrationTests` green (Docker).
