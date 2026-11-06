# Spec Delta

## ADDED Requirements

### Requirement: The distributed outbox lock is confined to one product

The Redis-leased outbox lock SHALL be held per product name, set with
`RedisCoordinationOptions.ProductName` — the same setting that confines replay coordination — so that
workers of different products on one store do not wait for each other. A worker that sets no name SHALL
use the unnamed lock, shared with other workers that set none and with earlier releases.

#### Scenario: Two products share one store

- **WHEN** a worker of one product holds the lock, and a worker of a product with a different name asks
  for it — verified on the Redis-backed lock
- **THEN** the second worker acquires its own lock

#### Scenario: Two workers of one product

- **WHEN** a worker holds the lock, and another worker with the same name asks for it
- **THEN** the second worker does not acquire it

#### Scenario: One worker of a product lacks the name

- **WHEN** a worker of a product sets no name and another worker of that product sets one
- **THEN** they do not exclude each other and may drain concurrently, which the at-least-once contract
  tolerates as duplicate publication, and the documentation says so
