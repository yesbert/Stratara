# Tasks

## 1. Failing tests first

- [x] 1.1 Add `tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Projections/ProjectionReplayProductNameTests.cs` on a real Redis: a request of one product reaches its hosts alone; active marking, progress, failure and outcome are invisible to the other product and visible to every host of the owner; two unnamed hosts still meet. Verify it fails against the fixed names with `--filter-class`.
- [x] 1.2 Extend `tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Outbox/RedisOutboxLockTests.cs`: different names both acquire, the same name excludes. Verify red.
- [x] 1.3 Add `tests/Stratara.Outbox.RabbitMQ.Tests/DependencyInjection/RedisCoordinationOptionsBindingTests.cs`: binding from the section through both registrations, refused names, and the unset names pinned literally. Verify red.

## 2. Implementation

- [x] 2.1 Add `src/Stratara.Outbox.RabbitMQ/RedisCoordinationOptions.cs` with the binding and validation, and call it from `AddProjectionReplayState()` and `AddRedisOutboxLock()`. Verify with the tests of 1.3.
- [x] 2.2 Derive every name in `src/Stratara.Outbox.RabbitMQ/Projections/ProjectionReplayState.cs` from the setting, including the keys passed to all three scripts. Verify with the tests of 1.1 and the existing `ProjectionReplayStateTests`, which pin the unnamed keys.
- [x] 2.3 Derive the key in `src/Stratara.Outbox.RabbitMQ/Outbox/RedisOutboxLock.cs` from the setting, for acquire and release. Verify with the tests of 1.2.

## 3. Documentation

- [x] 3.1 Document the setting, setting it on all hosts together, what a host without it does, and the keys left behind, in `docs/guides/write-a-projection.md`, `docs/guides/outbox-setup-rabbitmq.md` and `docs/getting-started/prerequisites.md`. Verify with `dotnet test tests/Stratara.Documentation.Tests`.
- [x] 3.2 Update `docs/reference/di-extensions-cheatsheet.md`, `src/Stratara.Outbox.RabbitMQ/README.md` and `llms.txt` (`llms-full.txt` through its generator if the tests demand it). Verify with `dotnet test tests/Stratara.Documentation.Tests`.

## 4. Integration checks

- [x] 4.1 `dotnet test tests/Stratara.Outbox.RabbitMQ.Tests` is green, and `ProjectionReplayStateTests`, `ProjectionReplayProductNameTests` and `RedisOutboxLockTests` are green one class at a time.
- [x] 4.2 `openspec validate --strict name-redis-coordination-per-product` passes.

## Workflow follow-up

- Archive after the owner's review.
