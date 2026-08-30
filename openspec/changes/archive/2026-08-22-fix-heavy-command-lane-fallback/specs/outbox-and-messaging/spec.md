## MODIFIED Requirements

### Requirement: Long-running commands travel a separate lane

A command that declares itself heavy SHALL be published to a separate topic with its own
subscription and its own worker, so that a flood of slow commands cannot starve interactive ones.
This SHALL hold on every path a command can take to the bus, including republication from durable
storage.

#### Scenario: A heavy command is dispatched

- **WHEN** a command declaring itself heavy is dispatched
- **THEN** it is published to the heavy lane's topic, not the shared command topic

#### Scenario: An ordinary command is dispatched

- **WHEN** a command that does not declare itself heavy is dispatched
- **THEN** it is published to the shared command topic

#### Scenario: A heavy worker runs

- **WHEN** the heavy-command worker runs
- **THEN** it consumes only the heavy lane's subscription, at a bounded degree of parallelism
- **AND** where the configured parallelism is not a positive number, it falls back to the processor
  count rather than to zero

#### Scenario: No heavy worker is listening

- **WHEN** a heavy command is published and no worker is bound to the heavy lane
- **THEN** the publish is rejected rather than silently discarded, and the command falls back to
  durable storage, where it waits until a heavy worker comes online

#### Scenario: A stored heavy command is republished

- **WHEN** a heavy command that fell back to durable storage is later republished
- **THEN** it goes to the heavy lane, whether or not its recorded type can be resolved at that moment
