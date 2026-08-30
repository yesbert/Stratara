# Tasks — The outbox drain stops when it stops making progress

Test-first throughout: each implementation task has a test task before it that fails against the
current code for the stated reason.

**Two existing tests pin the behaviour this change removes.** Neither is wrong today; both describe
the defect faithfully, which is why they pass. They must be rewritten as part of the change, not
quietly deleted, and the rewrite is a task in its own right so a reviewer sees it:

- `OutboxWorkerTests.ExecuteAsync_LockAcquiredWithBatches_DispatchesAndLoopsUntilEmpty` — its name is
  the requirement being amended. It feeds a queue ending in an empty batch, which is exactly the case
  the loop handles correctly.
- `OutboxWorkerTests.ExecuteAsync_RecordsPublishedEntries_TaggedByOutboxKind` — asserts the worker
  emits the counter. The counter moves to the dispatchers, so this assertion moves with it.

## 1. Cover the drain pass

- [ ] 1.1 In `tests/Stratara.Outbox.RabbitMQ.Tests/Outbox/OutboxWorkerTests.cs`, add a test that a
  batch which the dispatcher leaves untouched is read **once** in a pass — set the repository to
  return the same non-empty batch on every call and assert `GetManyAsync<CommandEnvelope>` is called
  once and the dispatcher once. Fails today: it loops forever. Give the test a hard timeout so a
  regression fails the run rather than hanging it.
- [ ] 1.2 Add the same test for `EventBundle`. Fails today for the same reason.
- [ ] 1.3 Add a test that an undeliverable command batch does not starve the event drain — the
  command repository returns the same batch every time, and the event dispatcher must still be
  invoked once in the same pass. Fails today: the command loop never returns.
- [ ] 1.4 Rewrite `ExecuteAsync_LockAcquiredWithBatches_DispatchesAndLoopsUntilEmpty` to describe one
  batch per pass, renaming it accordingly. Keep it asserting that both dispatchers receive their
  entries.
- [ ] 1.5 Run `dotnet test tests/Stratara.Outbox.RabbitMQ.Tests` and confirm 1.1–1.3 fail by timing
  out or exceeding the expected call count, not on harness setup.

## 2. Bound the drain pass

- [ ] 2.1 In `src/Stratara.Outbox.RabbitMQ/Outbox/OutboxWorker.cs`, replace the `while` loop in
  `HandleUnpublishedCommandsAsync` with a single read-and-dispatch, skipping the dispatch when the
  batch is empty.
- [ ] 2.2 The same in `HandleUnpublishedEventsAsync`.
- [ ] 2.3 Update the class XML docs: a pass handles one batch and undelivered entries are retried on
  the next interval. Remove any wording that promises draining until empty.
- [ ] 2.4 `dotnet test tests/Stratara.Outbox.RabbitMQ.Tests` green.

## 3. Cover the counter where publication happens

- [ ] 3.1 Add a test in `tests/Stratara.Outbox.RabbitMQ.Tests/Outbox/CommandOutboxDispatcherTests.cs`
  that draining stored entries records `outbox.published` tagged `command` with the number the bus
  accepted. Fails today: the dispatcher emits nothing.
- [ ] 3.2 Add a test that entries the bus rejects are **not** counted — a mixed batch where some
  publishes fail records only the accepted ones. This is the defect in one assertion. Fails today.
- [ ] 3.3 Add a test that a drain suppressed by an active replay records nothing at all. Fails today:
  the worker counted the whole batch.
- [ ] 3.4 The equivalents of 3.1–3.3 in
  `tests/Stratara.Outbox.RabbitMQ.Tests/Outbox/EventBundleOutboxDispatcherTests.cs`, tagged `event`.
- [ ] 3.5 Delete `ExecuteAsync_RecordsPublishedEntries_TaggedByOutboxKind` from `OutboxWorkerTests`,
  now covered by 3.1–3.4 at the layer that knows the answer.
- [ ] 3.6 Run `dotnet test tests/Stratara.Outbox.RabbitMQ.Tests` and confirm 3.1–3.4 fail because
  nothing is recorded, not because the meter listener saw nothing at all.

## 4. Move the counter

- [ ] 4.1 In `CommandOutboxDispatcher.EnqueueOutboxEntriesAsync`, record `OutboxEntriesPublished`
  tagged `command` for the entries the bus accepted — the same ones that get deleted. Keep the
  instrument name and the tag unchanged.
- [ ] 4.2 The same in `EventBundleOutboxDispatcher.EnqueueOutboxEntriesAsync`, tagged `event`.
- [ ] 4.3 Remove both `OutboxEntriesPublished` increments from `OutboxWorker`, and its now-unused
  diagnostics usings.
- [ ] 4.4 Confirm `ApplicationDiagnostics` itself is untouched — the instrument keeps its name,
  description and tags. A change there would be a breaking observability change and is not wanted.
- [ ] 4.5 `dotnet test tests/Stratara.Outbox.RabbitMQ.Tests tests/Stratara.Infrastructure.Tests`
  green — `Stratara.Infrastructure.Tests` holds its own outbox worker and dispatcher tests.

## 5. Close out

- [x] 5.1 `openspec validate bound-the-outbox-drain-by-progress --strict` clean.
- [x] 5.2 `./scripts/local-gauntlet.sh` green.
- [x] 5.3 `dotnet test tests/Stratara.Outbox.RabbitMQ.IntegrationTests` (Docker) green — the drain
  path has integration coverage that the gauntlet does not run.
- [x] 5.4 CHANGELOG entry under the next version, in consumer-visible terms: the drain no longer
  repeats an undeliverable batch, the published counter now counts deliveries, and the two shifts a
  consumer must know about — lower counter values, and a backlog draining one batch per interval.
