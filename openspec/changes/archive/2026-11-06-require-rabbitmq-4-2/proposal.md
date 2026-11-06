# Require RabbitMQ 4.2

> **Status:** approved (owner, 2026-11-06)

## Why

The consumer-visible effect today: on a broker below 4.2, a message for a subscription not yet rebound
after a broker restart is lost silently. A real broker restart showed that on 3.13, 4.0 and 4.1 a topic
exchange and its bindings are lost, while the queue survives. A subscription whose host is up rebinds on
recovery, which is enough for the broker to report a publication as routed, so nothing tells the publisher
that another subscription missed it. On 4.2, 4.3 and 4.3.6 the exchange and its bindings survive. The
documented floor of 3.13 therefore promises a delivery the framework cannot keep.

## What Changes

- **BREAKING for deployments below 4.2, in support only:** the minimum broker for
  `Stratara.Outbox.RabbitMQ` rises from 3.13 to 4.2. No production code changes and nothing refuses an older
  broker; a host below the floor must update the broker first.
- The documentation names the floor, the reason and the update step.
- A regression test restarts a real broker and asserts that a subscription without a running host still
  receives what is published afterwards.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `outbox-and-messaging`: adds the broker floor and what it guarantees across a restart.

## Impact

- Documentation: `docs/getting-started/prerequisites.md`, `docs/guides/outbox-setup-rabbitmq.md`, `llms.txt`
  (`llms-full.txt` through its generator if the tests demand it), `src/Stratara.Outbox.RabbitMQ/README.md`.
- Tests: `tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Messaging/RabbitMqBrokerRestartTests.cs`.
- Operations: a deployment below 4.2 updates its broker before taking this release.
- No legacy source is dissolved or superseded.
