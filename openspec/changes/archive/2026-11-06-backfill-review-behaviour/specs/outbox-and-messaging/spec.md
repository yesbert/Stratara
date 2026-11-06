## ADDED Requirements

### Requirement: A stored message that cannot be read does not stop the drain

A stored message that cannot be read back SHALL be logged as an error naming the entry and its type,
SHALL stay stored, and SHALL NOT keep the messages behind it from being published. Messages are drained
oldest first, so a failure on one would otherwise end every pass on the same row.

#### Scenario: One stored message cannot be read

- **WHEN** a drain pass reads a stored command or event bundle whose content cannot be read back, with
  readable messages behind it
- **THEN** the unreadable one is logged and stays stored, and those behind it are published and removed
  in the same pass

#### Scenario: Draining one kind fails as a whole

- **WHEN** a drain pass fails while draining stored commands
- **THEN** the failure is logged, and stored event bundles are still drained in that pass

### Requirement: Publishing does not accumulate connections to the broker

Publishing at a steady rate SHALL NOT use up the connection's capacity for send links. The Azure Service
Bus transport SHALL hold one sender per topic, opened on the first publish to it, and SHALL close the
senders it opened when the bus is disposed.

#### Scenario: Several messages are published to one topic

- **WHEN** a host publishes several messages to one topic
- **THEN** one sender is used for all of them, and another topic has one of its own — verified on the
  Azure Service Bus transport against a substituted client, not a live namespace

#### Scenario: The bus is disposed

- **WHEN** the bus is disposed
- **THEN** every sender it opened is closed — verified on the Azure Service Bus transport against a
  substituted client, not a live namespace
