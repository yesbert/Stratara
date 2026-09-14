## ADDED Requirements

### Requirement: A message a handler cannot take is retried a bounded number of times and then kept

Where a handler fails on a message from a durable subscription, the framework SHALL have the
transport deliver the message again a bounded number of times, and after the bound SHALL move it to
a dead-letter destination that belongs to that subscription, where an operator can inspect it and
return it to the subscription. A message on a durable subscription SHALL NOT be discarded by the
framework on any transport. A transient subscription — one that exists only while its process
listens — has nobody to return a message to and is outside this requirement.

A concurrency conflict SHALL be treated as a retry, not a failure, but SHALL be bounded as well: a
message that conflicts more often than the bound allows is moved to the same destination.

Both bounds SHALL be configurable with defaults, and every move to the dead-letter destination the
framework decides SHALL be recorded with the topic, the subscription and the reason, and counted as
a measurement dimensioned by topic and subscription. A move the broker makes on its own — its
backstop limit firing before the framework's decision — is the broker's to record.

The alternative — reject and drop — turns a handler bug into a silent loss after the caller was told
the command was accepted; a bundle that vanishes from one subscription leaves that side of the
system behind with nothing to replay.

#### Scenario: A handler keeps failing

- **WHEN** a handler throws on every delivery of a message
- **THEN** the message is delivered again until the bound is reached and is then found on the
  subscription's dead-letter destination, with the failure recorded and counted

#### Scenario: A handler fails once

- **WHEN** a handler throws on one delivery and succeeds on the next
- **THEN** the message is acknowledged on the successful delivery and is not dead-lettered

#### Scenario: A handler keeps conflicting

- **WHEN** a handler reports a concurrency conflict on every delivery of a message
- **THEN** the message is redelivered until the conflict bound is reached and is then found on the
  subscription's dead-letter destination, recorded as a conflict rather than a failure

#### Scenario: An operator returns a dead-lettered message

- **WHEN** an operator moves a message from the dead-letter destination back to its subscription
- **THEN** it is delivered to the subscription's consumers like any other message, with its retry
  count starting over

#### Scenario: A host configures the bounds

- **WHEN** a host configures a different number of deliveries or conflicts before dead-lettering
- **THEN** the configured bounds apply, and a host that configures nothing gets the defaults

## MODIFIED Requirements

### Requirement: The transport is replaceable

The framework SHALL address the bus through one abstraction, with implementations for more than one
broker, and SHALL let a host select one by registration order. No component above the transport
SHALL depend on which broker is in use — and that includes what happens to a message its handler
cannot take: the retry bound, the conflict bound and the dead-letter destination SHALL hold on every
broker the framework ships an implementation for.

#### Scenario: A host selects a different broker

- **WHEN** a host registers an alternative bus implementation after the default
- **THEN** that implementation is used, and dispatchers, workers and the outbox are unaffected

#### Scenario: A handler fails on either broker

- **WHEN** a handler exhausts the retry bound on a message
- **THEN** the message is found on the subscription's dead-letter destination whichever broker is in
  use — verified against a live RabbitMQ broker and against the Azure Service Bus emulator, not a
  live namespace

#### Scenario: Broker credentials are missing outside development

- **WHEN** a host publishes without credentials in any environment other than development
- **THEN** publishing fails loudly rather than falling back to the broker's default account
- **AND** the failure names the environment, so a host whose environment is not the one its operator
  assumed can tell

#### Scenario: Broker credentials are missing in development

- **WHEN** a host publishes without credentials in development
- **THEN** the default account is used and the fallback is recorded, so a local host runs without
  configuring credentials

#### Scenario: A host outside development wants the default account

- **WHEN** an operator genuinely wants the broker's default account outside development
- **THEN** they configure it by name like any other credential — the framework offers no implicit
  path to it
