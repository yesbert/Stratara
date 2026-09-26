## MODIFIED Requirements

### Requirement: A message a handler cannot take is retried a bounded number of times and then kept

Where a handler fails on a message from a durable subscription, the framework SHALL have the
transport deliver the message again a bounded number of times, and after the bound SHALL move it to
a dead-letter destination that belongs to that subscription, where an operator can inspect it and
return it to the subscription. A message on a durable subscription SHALL NOT be discarded by the
framework on any transport. A transient subscription — one that exists only while its process
listens — has nobody to return a message to and is outside this requirement.

A handler that fails with the event store's failure saying its events were committed but could not
be published has taken the message: the framework SHALL acknowledge it instead of delivering it
again or moving it to the dead-letter destination, whatever the subscription's own cancellation
says, and SHALL log the failure with the topic at error level. A second delivery would record the
same facts again. Every outcome of a handler SHALL be settled with the transport whatever the
subscription's own cancellation says. A subscription that stops SHALL stop taking messages and let
the handlers it is running settle theirs, for a bounded time, before it closes, so that a handler that
completed while the host stops — or whose save committed — is acknowledged rather than handed back to
run again; a host that stops SHALL count as stopped only once its stopping subscriptions have closed,
within its shutdown timeout, so their handlers settle while the services they use still exist. A
message the transport had already fetched but not yet handed to a handler SHALL go back to the queue
unhandled; the broker counts it as delivered once more, so the number of messages a subscription
fetches ahead of its handler SHALL be bounded, and where a transport fetches ahead the host SHALL be
able to configure that bound, with a default.

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

#### Scenario: A handler committed but could not publish

- **WHEN** a handler on a durable subscription fails because its save committed its events but could
  not hand their bundle on
- **THEN** the message is acknowledged after that one delivery, is not delivered again and is not
  found on the dead-letter destination, and the failure is logged with the topic — verified on
  RabbitMQ and on the Service Bus emulator

#### Scenario: The subscription stops while a handler runs

- **WHEN** a subscription is stopped while its handler is running, and the handler then completes or
  fails because its save committed but could not publish
- **THEN** the message is acknowledged and is not found on the queue afterwards, and a message
  published after the stop is not taken — verified on RabbitMQ and on the Service Bus emulator

#### Scenario: The host stops while a handler runs

- **WHEN** the host stops while a subscription's handler is running
- **THEN** the host counts as stopped only after the handler has settled its message, and the message
  is not found on the queue afterwards — verified on RabbitMQ and on the Service Bus emulator

#### Scenario: A handler never returns when the subscription stops

- **WHEN** a subscription stops while its handler never returns
- **THEN** the subscription still finishes stopping after a bounded wait, so neither the host's stop
  nor its disposal waits for the handler — verified on RabbitMQ and on the Service Bus emulator

#### Scenario: Messages were fetched but not yet handled when the subscription stops

- **WHEN** a subscription stops while messages it had fetched still wait for a handler
- **THEN** those messages are not handled and go back to the queue, marked as delivered once more —
  verified on RabbitMQ

#### Scenario: A host bounds how many messages a subscription fetches ahead

- **WHEN** a host configures how many messages a subscription may hold, and more than that wait on the
  queue while its handler runs
- **THEN** the subscription holds no more than that many and the rest stay on the queue, and a host
  that configures nothing gets the default — verified on RabbitMQ
