## MODIFIED Requirements

### Requirement: Read models are queried through a scoped unit of work

Read-side access SHALL be through a unit of work distinct from the write side's, so that a query
never participates in a write transaction and a read model can live in its own store.

Registering the read store's database context through the framework's registration SHALL make that
read-side unit of work available without a further registration by the consumer. A read-side unit
of work the consumer registers itself SHALL take precedence over the framework's.

#### Scenario: A projection writes to a read model

- **WHEN** a projection processes a bundle
- **THEN** it does so through the read-side unit of work, in its own transaction

#### Scenario: A consumer registers only the read context

- **WHEN** a consumer registers its read-store context through the framework's registration and
  nothing else
- **THEN** the read-side unit of work resolves, bound to that context

#### Scenario: A consumer supplies its own read-side unit of work

- **WHEN** a consumer registers its own read-side unit of work, before or after registering the
  context
- **THEN** the consumer's unit of work is the one resolved
