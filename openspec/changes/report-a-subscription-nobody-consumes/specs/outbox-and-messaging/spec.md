## ADDED Requirements

### Requirement: An established subscription that nobody consumes is reported

Where a host establishes a subscription that already holds at least a configurable number of messages and
has no consumer attached, the framework SHALL record a warning naming the topic, the subscription and the
number of messages it holds. The number SHALL be configurable with a default, and a value of zero SHALL turn
the report off. Attaching a handler to a subscription SHALL NOT be reported, whatever it holds, because a
backlog met by an attaching handler is a consumer catching up. The report SHALL NOT discard, expire or cap
anything: a message on a durable subscription is still kept until something consumes it.

Establishing subscriptions ahead of their handlers trades a silent loss for a growing queue, and a queue
whose handler is never deployed grows until the broker runs out of room. Reporting it where the subscription
is established puts the orphan in front of the operator at the next start of every host that publishes to it.

#### Scenario: A subscription with a backlog and no consumer is established

- **WHEN** a host establishes a subscription that holds at least the configured number of messages and no
  consumer is attached to it
- **THEN** a warning names the topic, the subscription and the number of messages, and nothing it holds is
  removed — verified on RabbitMQ

#### Scenario: A subscription with a consumer is established

- **WHEN** a host establishes a subscription that holds a backlog while a consumer is attached to it
- **THEN** nothing is reported

#### Scenario: A subscription below the threshold is established

- **WHEN** a host establishes a subscription that holds fewer messages than the configured number
- **THEN** nothing is reported

#### Scenario: The report is turned off

- **WHEN** the configured number is zero
- **THEN** no subscription is reported, whatever it holds

#### Scenario: A handler attaches to a subscription with a backlog

- **WHEN** a handler subscribes to a subscription that holds a backlog
- **THEN** nothing is reported, and the handler receives what was held

#### Scenario: A transport creates its subscriptions administratively

- **WHEN** the transport in use provisions subscriptions outside the application's lifetime
- **THEN** establishing one reports nothing, and the transport's own monitoring is where a backlog shows
