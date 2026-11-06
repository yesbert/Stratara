# Name Redis coordination per product

> **Status:** approved (owner, 2026-11-06)

## Why

The consumer-visible effect today: two products sharing one Redis run each other's projection replays.
Every name the framework writes to Redis is fixed, so a replay requested in one product is claimed by
whichever host of either product wins the claim, its outcome is reported to both, and while either
replays, the other suspends publication and refuses a single projection's rebuild. Measured on 4.7.1
with one product holding a truncator and the other none: of six requests in a row, three were claimed by
the wrong product and failed within 10 ms. Once both hold a truncator, one product's request empties the
other's read models.

## What Changes

- A new setting names the product: `RedisCoordinationOptions.ProductName`, section
  `RedisCoordination:ProductName`. One setting governs both the replay coordination and the outbox lock.
- With a name set, every Redis key and channel the framework writes carries it. Requests, the active
  marking, progress, outcome and the outbox lock are then confined to the hosts of that product.
- With no name set, today's fixed names stay, and an installation that sets nothing runs unchanged.
- A name outside letters, digits, `.`, `_` and `-`, or one of only whitespace, is refused when the host
  starts.
- Documentation states that the name is set on all hosts of a product together, and what happens to a
  host that does not have it. Not breaking: the API addition is additive and the default is unchanged.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `projections`: replay coordination in a shared store is confined to the hosts of one named product.
- `outbox-and-messaging`: the distributed lock is confined to the hosts of one named product.

## Impact

- `Stratara.Outbox.RabbitMQ`: a new public options type; the replay state and the Redis outbox lock read
  their names from it; `AddProjectionReplayState()` and `AddRedisOutboxLock()` bind and validate it.
- Documentation: `docs/guides/write-a-projection.md`, `docs/guides/outbox-setup-rabbitmq.md`,
  `docs/getting-started/prerequisites.md`, `docs/reference/di-extensions-cheatsheet.md`,
  `src/Stratara.Outbox.RabbitMQ/README.md`, `llms.txt`.
- Operations: naming a product moves its hosts to new names, so hosts with and without the name do not
  meet. Nothing is migrated: the old keys are left behind.
- No legacy source is dissolved or superseded.
