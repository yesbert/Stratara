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

## 3. Documentation

- [x] 3.1 `docs/guides/write-a-command-handler.md` and `docs/guides/use-resilience-policies.md`.
- [x] 3.2 `CHANGELOG.md` → *Unreleased*.

## 4. Verify

- [x] 4.1 `openspec validate tell-a-committed-save-from-a-failed-one --strict`.
- [x] 4.2 Local gauntlet green.
