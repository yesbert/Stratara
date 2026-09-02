## MODIFIED Requirements

### Requirement: Delivery is at least once, never at most once

A message that reaches durable storage SHALL be retried until the bus accepts it, and SHALL be
removed from storage only after acceptance. A handler MUST therefore be prepared to see the same
message more than once.

A stored message SHALL be counted as published only once the bus has accepted it, so that the count
reflects what was delivered rather than what was read from storage.

Delivery order across consumers is NOT guaranteed. Where a subscription is consumed by more than one
consumer, two messages published one after the other may be handed to different consumers and
processed at the same time, so a handler MUST NOT assume that a message about an entity arrives after
the message that created it. What a handler MAY rely on is the per-process serialisation the
projection and saga workers provide for bundles about one aggregate, and the way it can report a
prerequisite that has not been applied yet — both stated in the `projections` and `sagas`
capabilities. A host that needs strict order runs a single consumer.

#### Scenario: A stored message is published successfully

- **WHEN** a stored message is later published and the bus accepts it
- **THEN** it is removed from durable storage and counted as published

#### Scenario: Publishing a stored message fails again

- **WHEN** publishing a stored message fails
- **THEN** it stays in durable storage for a later attempt — it is never dropped, and it is not
  counted as published

#### Scenario: Two messages are consumed in parallel

- **WHEN** a subscription has several consumers and two messages are published in quick succession
- **THEN** they may be processed concurrently and in either order, and the framework makes no promise
  about which completes first — verified on the RabbitMQ transport, whose parallel consumers share
  one queue
