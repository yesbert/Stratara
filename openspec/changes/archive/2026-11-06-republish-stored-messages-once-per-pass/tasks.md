# Tasks

## 1. Commands

- [x] 1.1 Add to `tests/Stratara.Outbox.RabbitMQ.Tests/Outbox/CommandOutboxDispatcherTests.cs`, under a real retry pipeline: a refused stored entry costs one publish attempt per pass, every refused entry of a batch is attempted, and a dispatched command still retries; record the red run with `dotnet test tests/Stratara.Outbox.RabbitMQ.Tests`
- [x] 1.2 Republish from storage without the pipeline in `src/Stratara.Outbox.RabbitMQ/Outbox/CommandOutboxDispatcher.cs`, the first dispatch keeping it; verify that the tests of 1.1 pass

## 2. Event bundles

- [x] 2.1 Add the same tests to `tests/Stratara.Outbox.RabbitMQ.Tests/Outbox/EventBundleOutboxDispatcherTests.cs`, with a durable bundle among the paths that still retry; record the red run with the same command
- [x] 2.2 Republish from storage without the pipeline in `src/Stratara.Outbox.RabbitMQ/Outbox/EventBundleOutboxDispatcher.cs`; verify that the tests of 2.1 pass

## 3. Documentation

- [x] 3.1 Correct `OutboxOptions.LockLeaseSeconds` in `src/Stratara.Outbox.RabbitMQ/Outbox/OutboxOptions.cs` and the dispatch names in `src/Stratara.Resilience/Resilience/ResilienceNames.cs`; verify that `dotnet build` of both projects passes with CS1591 as an error
- [x] 3.2 State the one attempt in the backpressure section of `docs/guides/outbox-setup-rabbitmq.md` and in the pipeline table of `src/Stratara.Resilience/README.md`; verify with `dotnet test tests/Stratara.Documentation.Tests`
- [x] 3.3 Verify `openspec validate republish-stored-messages-once-per-pass --strict` passes

## Workflow follow-up

- The owner reviews the uncommitted diff; committing, pushing and the CHANGELOG entry are outside this task list.
- Archive the change after that review.
