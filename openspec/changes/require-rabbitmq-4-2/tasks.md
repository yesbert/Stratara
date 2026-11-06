# Tasks

## 1. Regression test

- [x] 1.1 Add `tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Messaging/RabbitMqBrokerRestartTests.cs` with its environment-variable override removed and the repository's image pinned. Verify both tests pass, once, with `--filter-class`.

## 2. Documentation

- [x] 2.1 Raise the floor to 4.2 with the reason and the update step in `docs/getting-started/prerequisites.md` and `docs/guides/outbox-setup-rabbitmq.md`, and change `llms.txt` and `src/Stratara.Outbox.RabbitMQ/README.md`. Verify `grep -rn "3\.13"` outside `CHANGELOG.md` and the archive finds no floor, and `dotnet test tests/Stratara.Documentation.Tests` is green.

## 3. Integration checks

- [x] 3.1 `openspec validate --strict require-rabbitmq-4-2` passes.

## Workflow follow-up

- Archive after the owner's review.
