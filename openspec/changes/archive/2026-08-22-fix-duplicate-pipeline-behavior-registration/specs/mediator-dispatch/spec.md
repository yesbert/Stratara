## MODIFIED Requirements

### Requirement: A stage is registered as an open generic and applies to every request

A cross-cutting stage SHALL be registered as an open generic type covering either request shape, and
SHALL be closed over each request type as it is dispatched. Registering a type that is not an open
generic of the right arity SHALL be rejected at registration time.

Registering the same stage more than once SHALL install it once. A host that composes overlapping
bundles of framework services must not silently run a stage twice.

#### Scenario: A valid stage is registered

- **WHEN** an open generic stage of the correct arity is registered
- **THEN** it resolves for every request of the matching shape

#### Scenario: An invalid stage is registered

- **WHEN** the registered type is absent, is not generic, or has the wrong number of type parameters
- **THEN** registration fails immediately with a message saying which shape was expected

#### Scenario: The same stage is registered twice

- **WHEN** the same open generic stage is registered more than once
- **THEN** it is installed once, and a dispatched request passes through it once
