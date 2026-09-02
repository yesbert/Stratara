# outbox-and-messaging Specification

## Purpose
Get work from the process that decided on it to the process that performs it without losing it —
including when the broker is down at the moment of the decision — and without a caller having to
know which of those two happened.

## Requirements

### Requirement: Dispatch attempts the bus first and falls back to durable storage

Dispatching SHALL attempt to publish to the message bus, and SHALL write the message to durable
storage only if that attempt fails. A caller SHALL NOT be able to observe which path was taken.

The outbox is therefore a fallback, not the primary route: the common case never touches the
database, and durability is what the fallback buys.

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

### Requirement: Delivery is at least once, never at most once

A message that reaches durable storage SHALL be retried until the bus accepts it, and SHALL be
removed from storage only after acceptance. A handler MUST therefore be prepared to see the same
message more than once.

A stored message SHALL be counted as published only once the bus has accepted it, so that the count
reflects what was delivered rather than what was read from storage.

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

#### Scenario: Two messages are consumed in parallel

- **WHEN** a subscription has several consumers and two messages are published in quick succession
- **THEN** they may be processed concurrently and in either order, and the framework makes no promise
  about which completes first — verified on the RabbitMQ transport, whose parallel consumers share
  one queue

### Requirement: A worker drains durable storage in batches under a distributed lock

A background worker SHALL periodically publish stored messages in batches, and SHALL hold a
lease-based lock while doing so, so that several instances of the same worker do not publish the same
message concurrently.

A drain pass SHALL be bounded by the work it can complete. Stored messages that could not be
published SHALL remain stored and be retried on a later pass, and SHALL NOT cause the current pass to
attempt them again. A pass SHALL NOT depend on storage coming back empty to end, because messages
that cannot be published never leave it.

#### Scenario: A worker acquires the lock

- **WHEN** the worker acquires the lock and stored messages exist
- **THEN** it publishes a batch of them and releases the lock afterwards

#### Scenario: A batch cannot be published

- **WHEN** a batch is handed to the dispatcher and none of it can be published
- **THEN** the pass ends rather than re-reading the same messages, the messages remain stored, and
  the next interval retries them

#### Scenario: Stored messages of one kind cannot be published

- **WHEN** stored messages of one kind cannot be published
- **THEN** stored messages of the other kind are still attempted in the same pass

#### Scenario: Another instance holds the lock

- **WHEN** the worker cannot acquire the lock
- **THEN** it records that and skips the pass entirely, rather than draining concurrently

#### Scenario: Nothing is stored

- **WHEN** the worker acquires the lock and no stored messages exist
- **THEN** no dispatch happens and the lock is released

#### Scenario: A pass fails

- **WHEN** a drain pass fails
- **THEN** the failure is recorded and the worker continues on its next interval

#### Scenario: No lock implementation is configured

- **WHEN** no distributed lock is registered
- **THEN** a lock that always grants is used — correct for a single-instance deployment, and unsafe
  for several, so a multi-instance deployment must register a real one

### Requirement: The lock is released only by its holder

A lock release SHALL affect only the lease the releasing instance acquired, so that an instance
whose lease expired cannot release a lock another instance has since taken.

#### Scenario: A lease is released

- **WHEN** an instance releases its lock
- **THEN** only its own lease is released, identified by a token it alone holds

#### Scenario: The lock service is unavailable

- **WHEN** the lock service cannot be reached, at acquisition or at release
- **THEN** the failure is recorded and treated as "not acquired" rather than propagating — an
  unavailable lock service does not take the worker down

### Requirement: Topics and subscriptions are configurable with defaults

Topic and subscription names SHALL be readable from configuration, and SHALL fall back to documented
defaults when unconfigured, so a host runs without naming them and can rename them without code
changes.

#### Scenario: No messaging configuration is supplied

- **WHEN** a host supplies no topic or subscription configuration
- **THEN** the framework uses its default names

#### Scenario: A name is configured

- **WHEN** a topic or subscription name is configured
- **THEN** that name is used

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

### Requirement: Publication is suppressed while a replay is in progress

While a projection replay is active, dispatch SHALL bypass the bus and write to durable storage, and
draining stored entries SHALL be suppressed entirely.

A replay re-emits historical events; publishing the commands they provoke would re-run side effects
that already happened.

#### Scenario: A command is dispatched during a replay

- **WHEN** a command is dispatched while a replay is active
- **THEN** the bus is not attempted and the command is written to durable storage

#### Scenario: The worker runs during a replay

- **WHEN** the dispatcher is asked to drain stored entries while a replay is active
- **THEN** it returns without publishing or deleting anything

### Requirement: A message carries its originating session and may be signed

Every dispatched message SHALL carry the session context under which it was created. Where a signer
is configured, it SHALL additionally carry a signature over the message's canonical form.

#### Scenario: A command is dispatched

- **WHEN** a command is dispatched
- **THEN** the message carries the correlation, causation, actor and data-owner identities of the
  session that produced it

#### Scenario: No session is set

- **WHEN** a command is dispatched with no session context
- **THEN** dispatch fails rather than producing an unattributable message

### Requirement: The transport is replaceable

The framework SHALL address the bus through one abstraction, with implementations for more than one
broker, and SHALL let a host select one by registration order. No component above the transport
SHALL depend on which broker is in use.

#### Scenario: A host selects a different broker

- **WHEN** a host registers an alternative bus implementation after the default
- **THEN** that implementation is used, and dispatchers, workers and the outbox are unaffected

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

### Requirement: A subscription can be established before anything is published to it

A host SHALL be able to establish a durable subscription without yet consuming from it, so that
every subscription on a topic exists before the first publication rather than from the moment its
handler attaches. Establishing a subscription SHALL be idempotent, and subscribing SHALL establish
it as well, so that a host which only ever subscribes keeps working unchanged.

A subscription that has been established SHALL receive everything published to its topic from that
point on, whether or not a handler is attached yet.

This closes a gap the delivery guarantee leaves open. Falling back to durable storage depends on the
transport reporting that a publication reached nobody, and on a topic with several subscriptions the
first one bound is enough for every publication to be reported as delivered. A subscription missing at
that moment is therefore indistinguishable from one that received the message, and the loss is silent.

#### Scenario: One subscription binds before another on the same topic

- **WHEN** two subscriptions share a topic, one is established, a message is published, and the second
  is established afterwards
- **THEN** the second subscription receives nothing published before it existed, and the publication
  is reported as successful — which is why establishing every subscription up front is the only
  protection

#### Scenario: Every subscription is established before publication begins

- **WHEN** every subscription on a topic is established during start-up, and a message is published
  before any handler has attached
- **THEN** each subscription receives that message once its handler attaches

#### Scenario: A subscription is established more than once

- **WHEN** a subscription that already exists is established again
- **THEN** nothing changes, and no message already held for it is lost or duplicated

#### Scenario: A transport creates its subscriptions administratively

- **WHEN** the transport in use provisions subscriptions outside the application's lifetime
- **THEN** establishing one is accepted and does nothing, because the subscription already exists
  before anything could publish

#### Scenario: A subscription is not durable

- **WHEN** a subscription is tied to the lifetime of its consumer's connection rather than to the
  topic
- **THEN** establishing it early is not offered, because a subscription that disappears with its
  connection cannot retain anything for a handler that has not attached
