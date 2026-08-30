# sagas Specification

## Purpose
Let a process that spans several aggregates react to what happened — issuing the next command when
an event arrives — without any aggregate knowing that process exists.

## Requirements

### Requirement: A saga declares the events it reacts to by handling them

A saga SHALL receive only the events it declares handlers for, determined from its handler
signatures. Handlers SHALL be found whether or not they are publicly visible, so a saga can keep its
reaction surface out of its public API.

#### Scenario: A bundle contains a mix of events

- **WHEN** a bundle contains events a saga handles and events it does not
- **THEN** only the handled ones are dispatched to it

#### Scenario: A saga handles nothing in the bundle

- **WHEN** no event in a bundle matches a saga's declared handlers
- **THEN** that saga is not dispatched at all

#### Scenario: A saga declares no handlers

- **WHEN** a saga declares no handlers
- **THEN** it reacts to nothing rather than to everything

#### Scenario: A handler is not publicly visible

- **WHEN** a saga declares a handler that is not public
- **THEN** it is still found and dispatched to

### Requirement: A handler may take the event payload or the enveloped event

A saga handler SHALL be invoked with the event payload where it declares one, and with the enveloped
event where it declares that instead.

#### Scenario: The saga declares a payload handler

- **WHEN** a relevant event arrives and the saga declares a handler taking its payload
- **THEN** that handler is invoked with the payload

#### Scenario: The saga declares only an enveloped handler

- **WHEN** no payload handler matches
- **THEN** the enveloped handler is invoked instead

### Requirement: Sagas run in parallel with each other, and in order within themselves

Every registered saga SHALL be dispatched a bundle concurrently with the others. Within one saga,
the events of a bundle SHALL be dispatched in the order they appear.

A saga must therefore not assume it is alone: two sagas reacting to the same event run at the same
time, and neither sees the other's effects.

#### Scenario: Several sagas react to one bundle

- **WHEN** a bundle arrives and several sagas find it relevant
- **THEN** they are dispatched concurrently

#### Scenario: One saga receives several events

- **WHEN** a bundle contains several events one saga handles
- **THEN** that saga receives them in the order they appear in the bundle

#### Scenario: No sagas are registered

- **WHEN** a bundle arrives and no sagas are registered
- **THEN** processing completes without error

### Requirement: Sagas consume the event stream through their own subscription

Saga processing SHALL subscribe to the event-bundle topic under a subscription of its own, separate
from the projection side's, so that each receives every bundle independently.

#### Scenario: A bundle is published

- **WHEN** an event bundle is published
- **THEN** both the saga side and the projection side receive it, neither consuming it from the other

#### Scenario: Only the saga worker is deployed

- **WHEN** a host runs the saga worker and no projection worker
- **THEN** sagas still receive every bundle

### Requirement: Sagas are discovered by assembly

Every concrete saga in a nominated assembly SHALL be registered, scoped to the processing of a
bundle. Abstract types and interfaces SHALL be skipped.

#### Scenario: An assembly is nominated

- **WHEN** a consumer nominates an assembly containing sagas
- **THEN** every concrete saga in it is registered, and abstract types and interfaces are not

### Requirement: Bundles arriving at sagas are verified like any other bus message

A bundle reaching the saga side SHALL be subject to the same envelope-integrity verification and the
same parsing bounds as any other consumed message.

#### Scenario: An unsigned bundle arrives in strict mode

- **WHEN** an unsigned or tampered bundle reaches the saga side and strict verification is configured
- **THEN** it is refused before any saga sees it

#### Scenario: An oversized bundle arrives

- **WHEN** a bundle exceeds the configured size limit
- **THEN** it is refused before being parsed

### Requirement: Saga processing is measured

The framework SHALL report how many bundles sagas are processing at any moment, how many events they
have processed, and how long bundle processing takes — each dimensioned by outcome.

#### Scenario: An operator watches saga load

- **WHEN** bundles are being processed
- **THEN** the in-flight count, the processed-event count and the processing duration are observable,
  with successes distinguishable from failures
