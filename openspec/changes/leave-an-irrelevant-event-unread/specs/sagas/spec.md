## MODIFIED Requirements

### Requirement: A saga declares the events it reacts to by handling them

A saga SHALL receive only the events it declares handlers for, determined from its handler
signatures. Handlers SHALL be found whether or not they are publicly visible, so a saga can keep its
reaction surface out of its public API.

An event that no saga in the host declares a handler for SHALL NOT be read. Its type SHALL NOT be
resolved and its payload SHALL NOT be decrypted, so it SHALL NOT need to be registered in the host, and
it SHALL fail neither the bundle it arrives in nor a store reader. Whether a saga handles an event
SHALL be decided on the event's type as it stands after upcasting. An event whose type does not
resolve, but whose name without namespace or assembly is the name of a type a saga in the host
handles, SHALL be read, and SHALL fail as an unregistered type fails. Where the host replaces how
recorded events are mapped, every event SHALL be read as before; where it replaces the component
that dispatches to sagas, that component SHALL receive every event of the bundle. A stateful process is decided by its own requirement.

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

#### Scenario: A bundle holds an event no saga handles, of a type the host never registered

- **WHEN** a bundle arrives holding an event no saga in the host handles, whose type the host never
  registered, beside events a saga does handle
- **THEN** the handled events are dispatched, and the bundle is treated as processed

#### Scenario: A saga reader meets such an event

- **WHEN** a stateless saga reads the store on the Orleans execution model, and its partition holds an
  event no saga in the host handles, of a type the host never registered
- **THEN** the reader reads past it rather than stalling the partition

#### Scenario: An unreadable event carries the name of a handled type

- **WHEN** an event's type does not resolve, and its name without namespace or assembly is the name of
  a type a saga in the host handles
- **THEN** the bundle or the reader fails, naming the type, as it does for any unregistered type

### Requirement: A saga can be a stateful process with a correlation and a timeout

On the Orleans execution model a saga MAY opt in to state: it declares which events it handles and
how it correlates them, and the framework SHALL keep its state per correlation in an event stream of
its own, so that it needs no storage of its own to configure, SHALL rehydrate it from that stream, and
SHALL let it register a timeout that fires once per cluster on or after its due time and survives a
restart. A fact MAY reach a process more than once, and a timeout MAY reach it for a step whose events
were not recorded; the framework SHALL hand every delivery the state as recorded, so that the process
decides from it. A saga that does not opt in SHALL keep running unchanged and stateless, as the
contract says. A fact SHALL reach a process under the session recorded with it, in place before the
process's state is read, so that a state stream reached through a connection routed per tenant is
read under the fact's tenant. A timeout has no fact to take a session from: it SHALL run under the
session the process's state stream was created with, which is read before that session can be in
place, and the documentation SHALL say so where a process's timeouts are described, so that a
consumer routing per tenant knows the timeout path reads the state stream under no tenant.

A process decides from the event itself whether it handles it, so its reader SHALL read every event
whose type resolves, after upcasting. An event whose type does not resolve cannot be one a process
handles. The reader SHALL skip it rather than stall, and SHALL log a warning naming the event's type,
once per host for each type — unless its name, without namespace or assembly, is the name of a type
the process declares a handler for, in which case it SHALL be read and fail as an unregistered type
fails.

#### Scenario: A process times out after a restart

- **WHEN** a process registers a timeout and the silo is killed before it is due
- **THEN** the timeout fires after the restart, once, and the process handles it with its
  rehydrated state — verified with three kills on the PostgreSQL store

#### Scenario: A stateless saga is registered beside a process

- **WHEN** a host registers an existing stateless saga and a stateful process
- **THEN** the stateless saga behaves as it did on the bus, one instance per fact, no state kept

#### Scenario: A fact reaches a process twice

- **WHEN** a fact is delivered to a process again after a crash
- **THEN** the process receives it with state that already reflects its first handling, if that
  handling was recorded

#### Scenario: A fact reaches a process through a tenant-routed store

- **WHEN** a process's state stream is reached through a service that takes the tenant when it is
  constructed, and a fact of that tenant is handed to the process
- **THEN** the state is read and the emitted events are appended under the fact's tenant — verified
  on the PostgreSQL store

#### Scenario: A process's reader meets an event whose type does not resolve

- **WHEN** a process reads the store and its partition holds an event whose type the host never
  registered
- **THEN** the reader reads past it, and a warning names the type once — verified on the in-process
  execution-model host over SQLite
