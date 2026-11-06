# Spec Delta

## MODIFIED Requirements

### Requirement: Replay coordination does not require shared infrastructure in a single-process host

The framework SHALL hold the replay coordination state — the active marking, the progress counters,
the failure message and the replay-request channel — in process where the host registers no shared
coordination store, and in that shared coordination store where it registers one. A host SHALL start and dispatch
in either case; the absence of a shared coordination store SHALL NOT be a start-up or first-dispatch
failure.

A host that holds the state in process SHALL record, once at start-up, that replay coordination is
confined to that process, so that an operator running several hosts learns it from the log rather
than from a side effect that was not suppressed.

#### Scenario: A host registers no shared coordination store

- **WHEN** a host composes any role that carries a dispatcher and registers no shared coordination
  store
- **THEN** the host starts, commands and bundles are dispatched, and a replay requested in that host
  runs in that host with its progress readable there

#### Scenario: A host registers a shared coordination store

- **WHEN** a host registers a shared coordination store before or after composing its role
- **THEN** the replay state is held in that shared coordination store, and a replay requested in one
  host is seen as active by every host sharing it under the same product name, exactly as before this
  requirement existed

#### Scenario: A host falls back to in-process coordination

- **WHEN** a host holds the replay state in process
- **THEN** it records a warning once, at start-up, saying that replay coordination is per process
  and naming what registers the shared coordination store

## ADDED Requirements

### Requirement: Replay coordination in a shared store is confined to one product

Replay coordination in a shared coordination store SHALL be confined to the hosts sharing one product
name, set with `RedisCoordinationOptions.ProductName`. Hosts with different names SHALL NOT see each
other's requests, active marking, progress, failure or outcome. Wherever this capability speaks of hosts
sharing the coordination store, it means hosts sharing the store under the same name.

#### Scenario: Two products share one store

- **WHEN** a replay is requested in a product, and another product with a different name shares the
  store — verified on the Redis-backed coordination store
- **THEN** only hosts of the requesting product receive the request and can claim it

#### Scenario: A running replay stays within its product

- **WHEN** a replay of one product is active or has ended
- **THEN** every host of that product sees its marking, progress and outcome, and hosts of the other
  product see none of them and keep dispatching

### Requirement: A host that sets no product name keeps the unnamed coordination

A host that sets no product name SHALL coordinate under the framework's unnamed coordination, unchanged
from releases without the setting, and SHALL meet other hosts that set none and hosts of earlier releases.
A host with a name SHALL NOT meet any of them, so a product's hosts MUST be given the name together.

#### Scenario: Nothing is set

- **WHEN** hosts set no product name
- **THEN** they coordinate with each other and with hosts of earlier releases as before

#### Scenario: One host of a product lacks the name

- **WHEN** a host of a product sets no name and another host of that product sets one
- **THEN** the two do not coordinate: neither receives the other's requests or sees its marking, so the
  unnamed host keeps publishing during the named host's replay, and the documentation says so

### Requirement: An unusable product name is refused at start

A product name of only whitespace, or holding anything other than letters, digits, `.`, `_` and `-`,
SHALL be refused when the host starts, with a message naming the setting. An empty or absent name SHALL
mean that no name is set.

#### Scenario: A name holds a separator

- **WHEN** a host sets a product name holding a character outside the permitted set
- **THEN** the host fails at start with a message naming `RedisCoordination:ProductName`
