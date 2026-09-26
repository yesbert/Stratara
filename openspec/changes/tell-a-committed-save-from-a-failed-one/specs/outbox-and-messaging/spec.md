## ADDED Requirements

### Requirement: A message whose handler committed its events is not delivered again

Where a handler fails with the event store's failure that says its events were committed but could
not be published, the framework SHALL acknowledge the message instead of delivering it again or
moving it to the dead-letter destination, and SHALL log the failure with the topic at error level. A
second delivery would record the same facts again; the events are already durable, and what is
missing is their publication, which a republish or a replay restores.

#### Scenario: A handler committed but could not publish

- **WHEN** a handler on a durable subscription fails because its save committed its events but could
  not hand their bundle on
- **THEN** the message is acknowledged after that one delivery, is not delivered again and is not
  found on the dead-letter destination, and the failure is logged with the topic — verified on
  RabbitMQ and on the Service Bus emulator
