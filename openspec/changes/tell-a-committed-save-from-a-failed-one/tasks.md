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

## 3. Documentation

- [x] 3.1 `docs/guides/write-a-command-handler.md` and `docs/guides/use-resilience-policies.md`.
- [x] 3.2 `CHANGELOG.md` → *Unreleased*.

## 4. Verify

- [x] 4.1 `openspec validate tell-a-committed-save-from-a-failed-one --strict`.
- [ ] 4.2 Local gauntlet green.
