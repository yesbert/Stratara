## ADDED Requirements

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
