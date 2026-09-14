## MODIFIED Requirements

### Requirement: Dispatch attempts the bus first and falls back to durable storage

Dispatching a command SHALL attempt to publish to the message bus, and SHALL write the message to
durable storage only if that attempt fails. A caller SHALL NOT be able to observe which path was
taken.

For an event bundle the same holds by default. A host MAY instead opt in to durable bundles: the
bundle is then written to durable storage in the same transaction that commits its events, the bus
is attempted after the commit, and the stored bundle is removed once the bus has accepted it — so
that from the moment the events are durable, the bundle is durable too. A caller SHALL NOT be able
to observe which path was taken here either; what it can observe is the guarantee stated under
*Delivery is at least once, never at most once*.

The outbox is therefore a fallback for commands, and for bundles a fallback or a write-ahead
record, as the host chooses: the common case never touches the database unless the host has decided
that a committed fact must never be left behind.

#### Scenario: The bus accepts the message

- **WHEN** the bus accepts a dispatched message
- **THEN** nothing is written to durable storage

#### Scenario: The bus rejects or is unreachable

- **WHEN** publishing fails for any reason
- **THEN** the failure is recorded and the message is written to durable storage, and the caller's
  dispatch still succeeds

#### Scenario: The caller needs a handle on the dispatch

- **WHEN** a command is dispatched
- **THEN** the caller receives the identity assigned to it, whichever path it took

#### Scenario: A host has opted in to durable bundles and the bus accepts

- **WHEN** a save commits events on a host with durable bundles and the bus accepts the bundle
- **THEN** the bundle was in durable storage when the events became durable, and is removed after
  acceptance — verified on the PostgreSQL store

#### Scenario: A host has opted in to durable bundles and the bus refuses

- **WHEN** a save commits events on a host with durable bundles and the bus refuses the bundle
- **THEN** the bundle stays in durable storage for the drain, and the save succeeds without a second
  write

### Requirement: Delivery is at least once, never at most once

A message that reaches durable storage SHALL be retried until the bus accepts it, and SHALL be
removed from storage only after acceptance. A handler MUST therefore be prepared to see the same
message more than once.

A stored message SHALL be counted as published only once the bus has accepted it, so that the count
reflects what was delivered rather than what was read from storage.

What reaches durable storage is the boundary of this guarantee, and by default an event bundle
reaches it only when the bus refuses it. Between the commit of the events and the bundle's arrival
at the bus or in storage there is a window in which the process may end, and a bundle lost in that
window is lost for every subscription: the events are in the store, and no projection and no saga
is told. A host on the default path MUST treat this as possible and repair by a replay for
projections; a saga has no such repair. A host that has opted in to durable bundles
(*Dispatch attempts the bus first and falls back to durable storage*) closes the window: a bundle
whose events are committed is in durable storage until the bus has accepted it, and a process that
ends anywhere after the commit leaves a bundle the drain delivers.

Delivery order across consumers is not guaranteed. Where a subscription is consumed by more than one
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

#### Scenario: The process ends between commit and publish on the default path

- **WHEN** a save has committed events and the process ends before the bundle reached the bus or
  durable storage
- **THEN** the events are in the store and the bundle is delivered to no subscription — the window
  the default path leaves open, measured on the RabbitMQ transport with the PostgreSQL store

#### Scenario: The process ends between commit and publish with durable bundles

- **WHEN** a save has committed events on a host with durable bundles and the process ends before
  the bus accepted the bundle
- **THEN** the bundle is in durable storage and the drain delivers it — verified on the PostgreSQL
  store with the RabbitMQ transport

#### Scenario: Two messages are consumed in parallel

- **WHEN** a subscription has several consumers and two messages are published in quick succession
- **THEN** they may be processed concurrently and in either order, and the framework makes no promise
  about which completes first — verified on the RabbitMQ transport, whose parallel consumers share
  one queue
