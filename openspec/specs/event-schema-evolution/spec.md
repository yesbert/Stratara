# event-schema-evolution Specification

## Purpose
Let a consumer change its event types without stranding the events already written — by resolving
recorded type names independently of the assembly version that wrote them, and by transforming an
old payload into the current shape on the way in.

## Requirements

### Requirement: A recorded type name resolves independently of the assembly version

Resolving a type from a recorded name SHALL ignore the assembly version, culture and public key
recorded with it, matching on the type name and assembly name alone.

Every event ever written carries the assembly version of the build that wrote it. Matching on the
full name would strand every event on the next version bump.

#### Scenario: An event was written by an earlier build

- **WHEN** an event recorded by a build with a different assembly version is read
- **THEN** its type resolves to the current type

#### Scenario: A type name records no assembly at all

- **WHEN** a recorded name carries no assembly qualification
- **THEN** it is used as recorded rather than rejected

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

### Requirement: An upcaster transforms one schema hop

A consumer SHALL be able to register a transformation from one recorded event type name to another,
rewriting the payload as it goes. Registration SHALL be by source type name, and that name need not
correspond to a type that still exists.

Not requiring the source type to exist is what makes a rename possible: the old type is gone, and
only its recorded name remains.

#### Scenario: An event type was renamed and its payload reshaped

- **WHEN** an event recorded under an old type name is read and an upcaster is registered for that
  name
- **THEN** the event is read as the upcaster's target type, with the payload the upcaster produced

#### Scenario: The source name differs only by assembly version

- **WHEN** a recorded name matches an upcaster's source name except for the assembly version
- **THEN** the upcaster still matches

### Requirement: Upcasters chain to a fixed point

Where the target of one upcaster is the source of another, the framework SHALL apply them in
sequence until no further upcaster matches, so that a consumer writes one transformation per schema
hop rather than one per historical version.

#### Scenario: Three schema versions exist

- **WHEN** an event recorded under the oldest of three schema versions is read, with one upcaster
  per hop registered
- **THEN** both upcasters are applied in order and the event is read as the newest type

### Requirement: An ambiguous or cyclic chain is refused

Registering two upcasters with the same source SHALL fail at composition time. A chain that returns
to a name it has already passed through SHALL fail when it is applied. An upcaster that produces no
payload SHALL fail.

#### Scenario: Two upcasters claim the same source

- **WHEN** two registered upcasters declare the same source event type name
- **THEN** composition fails, naming the duplicated source

#### Scenario: A chain loops

- **WHEN** applying a chain revisits a name it has already transformed
- **THEN** it fails, naming where the cycle was detected

#### Scenario: An upcaster produces nothing

- **WHEN** an upcaster returns no payload
- **THEN** it fails, naming the upcaster's source

### Requirement: Reading is unaffected when nothing matches

Where no upcasters are registered, or none matches the recorded name, or the recorded payload cannot
be parsed as structured data, reading SHALL proceed with the recorded name and payload unchanged.

#### Scenario: No upcasters are registered

- **WHEN** an event is read and no upcaster is registered at all
- **THEN** the recorded name and payload are used as they are

#### Scenario: No upcaster matches

- **WHEN** upcasters are registered but none declares this event's recorded name as its source
- **THEN** the recorded name and payload are used as they are

### Requirement: Upcasting applies on every read path but not to snapshots

Upcasting SHALL be applied before type resolution on every path that reads a recorded event —
whether from the store or from a message that crossed the bus. It SHALL NOT be applied to snapshots.

#### Scenario: An event is read from the store

- **WHEN** an event is read from a stream
- **THEN** upcasting runs before the type is resolved

#### Scenario: An event arrives over the bus

- **WHEN** an event arrives as a message rather than from the store
- **THEN** upcasting runs before the type is resolved, identically

#### Scenario: An aggregate is restored from a snapshot

- **WHEN** a snapshot written under an older aggregate shape is restored
- **THEN** no upcasting is applied to it — a snapshot is derived state, not a recorded fact, and a
  stale one fails to restore rather than being transformed

### Requirement: Upcasting operates on the payload as it was stored

An upcaster SHALL receive the payload in the form it was persisted. Where fields were marked for
encryption, those field values are ciphertext at that point and are not readable by the upcaster.

An upcaster can therefore rename, move or restructure a protected field, but cannot inspect or
transform its value.

#### Scenario: An upcaster reshapes a protected field

- **WHEN** an upcaster moves or renames a field that was marked for encryption
- **THEN** the transformation applies to the field's position and name, and its value remains
  ciphertext throughout
