# Stratara.Outbox.RabbitMQ.IntegrationTests

Integration suite for the broker- and Redis-coupled pieces of `Stratara.Outbox.RabbitMQ`.
Currently covers `RedisOutboxLock` against a real Redis brought up via Testcontainers.

## Requirements

- Docker (or a Testcontainers-compatible alternative such as Podman / Colima) reachable
  on the test host. The fixture starts a `redis:7-alpine` container per test collection.

## Local run

```bash
dotnet test tests/Stratara.Outbox.RabbitMQ.IntegrationTests
```

`./scripts/local-gauntlet.sh` does **not** run this project — the gauntlet filters out
`*IntegrationTests` so it stays Docker-free and fast.

## CI

Runs on `azure-pipelines-integration-tests.yml` (pipeline id pending registration).
That pipeline is intentionally non-blocking for PR merges until Docker support on the
hosted-Ubuntu image is empirically validated; once stable, it can be promoted to a
required PR policy.

## Conventions

- One xUnit collection fixture per piece of shared infrastructure
  (`RedisFixture` here). The fixture exposes a single connection multiplexer and a
  `FlushAsync()` helper that tests call before each scenario to reset state.
- Tests are flagged via the project's `*IntegrationTests` suffix, not via `[Trait]` —
  the directory pattern is the boundary recognised by `local-gauntlet.sh` and
  `azure-pipelines-publish.yml`.
