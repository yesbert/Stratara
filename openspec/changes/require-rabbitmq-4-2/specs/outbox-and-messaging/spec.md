# Spec Delta

## ADDED Requirements

### Requirement: The RabbitMQ transport requires a broker that keeps its routing across a restart

`Stratara.Outbox.RabbitMQ` SHALL require RabbitMQ 4.2 or later. On such a broker, a worker subscription
SHALL stay routed across a broker restart whether or not a host of it is running, so that what is published
afterwards waits for it. The framework SHALL NOT refuse an older broker; below the floor this is not guaranteed.

#### Scenario: A subscription established ahead of its handler

- **WHEN** a worker subscription is established without a handler, the broker restarts and a message is
  published — verified on RabbitMQ only, against a real broker restart
- **THEN** the message waits for the subscription and reaches the handler that later attaches

#### Scenario: A subscription whose host is down over the restart

- **WHEN** a worker subscription's host is down while the broker restarts and a message is published
- **THEN** the message waits in that subscription's queue

#### Scenario: A broker below the floor

- **WHEN** a host runs against a broker below 4.2
- **THEN** the documentation states that a subscription not yet rebound can silently lose a message after
  a broker restart, and that the broker must be updated first
