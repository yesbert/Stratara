## MODIFIED Requirements

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
