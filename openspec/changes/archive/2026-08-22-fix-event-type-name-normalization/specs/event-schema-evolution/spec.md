## MODIFIED Requirements

### Requirement: Only registered types are resolvable

Resolving SHALL succeed only for types the application has registered, and SHALL fail otherwise
naming the type and the ways to register it. Registering a different type under a recorded name that
is already taken SHALL be rejected, naming both types.

Deserializing into whatever type a recorded name happens to designate is how a store becomes a code
execution surface; the allowlist is what closes that. A registration that is quietly dropped when the
name is already taken is indistinguishable from one that never happened — the type simply fails to
resolve later, when a stored row is read, with nothing to say why.

#### Scenario: A registered type is resolved

- **WHEN** a type registered through handler, projection, saga or aggregate discovery, or
  explicitly, is resolved
- **THEN** resolution succeeds

#### Scenario: An unregistered type is resolved

- **WHEN** a recorded name designates a type the application never registered
- **THEN** resolution fails, naming the type and every registration route

#### Scenario: A caller wants to probe rather than fail

- **WHEN** a caller needs to know whether a name resolves without failing if it does not
- **THEN** a non-throwing form is available

#### Scenario: The same type is registered twice

- **WHEN** a type already registered is registered again
- **THEN** the second registration is accepted and changes nothing

#### Scenario: Two different types claim one recorded name

- **WHEN** a type is registered under a recorded name already held by a different type
- **THEN** registration fails, naming both, rather than keeping one and discarding the other
