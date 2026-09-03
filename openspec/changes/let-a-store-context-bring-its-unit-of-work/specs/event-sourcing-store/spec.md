## ADDED Requirements

### Requirement: Registering the store's context makes the store usable

Registering the write store's database context through the framework's registration SHALL make the
write-side unit of work available to everything that depends on it — appending events, publishing
through the outbox, handling commands in a worker — without a further registration by the consumer.
A write-side unit of work the consumer registers itself SHALL take precedence over the framework's.

A store whose context is registered but whose unit of work is not is a store that fails at the first
command with an error naming a type no guide mentions. The registration that declares the context
is the one place that knows which context the unit of work should be built over.

#### Scenario: A consumer registers only the write context

- **WHEN** a consumer registers its write-store context through the framework's registration and
  nothing else
- **THEN** the write-side unit of work resolves and is bound to that context, and a command that
  appends an event can be handled

#### Scenario: A consumer supplies its own write-side unit of work

- **WHEN** a consumer registers its own write-side unit of work, before or after registering the
  context
- **THEN** the consumer's unit of work is the one resolved

#### Scenario: The registration is applied more than once

- **WHEN** the write-store context is registered more than once for the same context type
- **THEN** one unit of work is resolved, bound to that context
