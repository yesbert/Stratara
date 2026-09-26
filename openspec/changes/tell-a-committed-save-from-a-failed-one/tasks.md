## 1. Reproduce first

- [x] 1.1 `tests/Stratara.Infrastructure.Tests/EventSourcing/EventSourceSaveOutcomeTests.cs`, against the
  SQLite test store:
  - a save with nothing staged publishes no bundle (fails on `main`, which publishes an empty one);
  - a save whose bundle handover throws after the commit fails with
    `CommittedEventsNotPublishedException` naming the stream, and the events are in the store (fails on
    `main`, which throws the handover's exception).
- [x] 1.2 `tests/Stratara.Shared.Tests/Resilience/ResilienceFactoryTests.cs`: the command and bundle
  dispatcher pipelines run `CommittedEventsNotPublishedException` once.

## 2. The fix

- [x] 2.1 `CommittedEventsNotPublishedException` in `src/Stratara.Abstractions/Abstractions/EventSourcing/`,
  fully documented, with the stream ids and the event count.
- [x] 2.2 `EventSource`: store and publish no bundle when nothing is staged (the session is still
  checked); wrap a failure of the handover after the commit, cancellation excepted.
- [x] 2.3 `ResilienceFactory`: the dispatcher retry does not handle the new exception.
- [x] 2.4 `IEventSource.SaveChangesAsync` documents both.

## 2b. Nothing runs committed work again (found in review)

- [x] 2b.1 `EventSource` wraps a cancelled handover after the commit too
  (`EventSourceSaveOutcomeTests.A_handover_cancelled_after_the_commit_still_says_the_events_are_committed`).
- [x] 2b.2 `RabbitMqBus` and `AzureServiceBusBus` acknowledge the message and log `108_113`
  (`RabbitMqDeadLetterTests.HandlerCommittedButCouldNotPublish_…`,
  `ServiceBusTests.SubscribeAsync_HandlerCommittedButCouldNotPublish_MessageIsCompleted`; the RabbitMQ one
  runs the handler three times on `main`).
- [x] 2b.3 `CommandExecution.RunIntentAsync` completes the intent and logs `117_127`
  (`IntentCommittedNotPublishedTests`, which fails without the change).
- [x] 2b.4 `ResilienceNames.MessageBus` and `ProjectionReplayBatch` exclude it too; the dispatcher
  pipelines still retry an ordinary failure and not cancellation (`ResilienceFactoryTests`).
- [x] 2b.5 `RabbitMqDeadLetterTests.MeterCapture` reads the metric names before its listener starts, so
  the metrics' first initialisation does not call back into a half-built listener.

- [x] 2b.6 A framework exception crosses silos with its type: the execution model's registrations add
  `Stratara` to Orleans' `ExceptionSerializationOptions.SupportedNamespacePrefixes`, and the properties of
  `ConcurrencyException` and `CommittedEventsNotPublishedException` read empty rather than null after a
  crossing (`FrameworkExceptionSerializationTests`; without the registration the round trip fails).

- [x] 2b.7 Round 3 of the review: the transports settle with `CancellationToken.None`; a durable host does
  not fail a save whose handover fails after the commit (`EventSourceSaveOutcomeTests`); a store reader
  counts such an entry as applied (`StoreReaderLoopTests`) and a durable timer as fired
  (`CommittedTimerTests`, integration); the RabbitMQ test proves the message was acknowledged by closing
  the consumer and finding the queue empty, the Service Bus one by peeking the subscription; the
  existing requirements that promised a retry carry the carve-out (MODIFIED deltas); log ids
  `117_127`–`117_129` in the schema page; "republish" is gone from the docs, which the framework does
  not offer.

- [x] 2b.8 Round 4 of the review: every exception type crosses silos, so a provider's inner failure does not
  stop a framework exception from crossing (`FrameworkExceptionSerializationTests` with a real Npgsql chain), a
  host's own filter kept; the prefix is `Stratara.`; the framework exceptions' properties read empty after a
  crossing; a timer's unregister after committed work ignores the stopping token; a stopping RabbitMQ
  subscription cancels its consumer and waits for running handlers to settle before closing the channel, and
  every settlement uses `CancellationToken.None` (`RabbitMqDeadLetterTests.SubscriptionStopsDuringTheHandler_…`,
  which found the message back in the queue before); the durable-host test checks the events committed.

## 3. Documentation

- [x] 3.1 `docs/guides/write-a-command-handler.md` and `docs/guides/use-resilience-policies.md`.
- [x] 3.2 `CHANGELOG.md` → *Unreleased*.

## 4. Verify

- [x] 4.1 `openspec validate tell-a-committed-save-from-a-failed-one --strict`.
- [x] 4.2 Local gauntlet green.
