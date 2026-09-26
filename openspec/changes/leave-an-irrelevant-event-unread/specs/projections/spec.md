## MODIFIED Requirements

### Requirement: A projection declares the events it cares about by handling them

A projection SHALL be dispatched only the events it declares handlers for, determined from the
handler signatures themselves rather than from a separate registration. The one exception: a
projection that declares it forgets deleted tenants SHALL also be dispatched the framework's
tenant-deletion facts, as *A projection can forget a deleted tenant* describes.

An event that no projection in the host is dispatched SHALL NOT be read. Its type SHALL NOT be resolved
and its payload SHALL NOT be decrypted, so it SHALL NOT need to be registered in the host. It SHALL
fail neither the bundle it arrives in, nor a replay, nor a store reader. Whether a projection is
dispatched an event SHALL be decided on the event's type as it stands after upcasting. An event whose
type does not resolve, but whose name without namespace or assembly is the name of a type a projection
in the host is dispatched, SHALL be read, and SHALL fail as an unregistered type fails.

A host that runs projections receives events it has no use for: types retired from an aggregate,
framework events another host's projections read, facts of streams it was never meant to understand.
A projection that ignores such an event is only as safe as the host's ability not to read it. The
exception for a matching name keeps a handled type that was moved without an upcaster failing loudly,
as it always has.

#### Scenario: A bundle contains a mix of events

- **WHEN** a bundle contains events a projection handles and events it does not
- **THEN** only the handled ones are passed to it

#### Scenario: A bundle contains nothing a projection handles

- **WHEN** no event in a bundle is relevant to a projection
- **THEN** that projection is not invoked at all, and the skip is recorded at debug level

#### Scenario: Several projections are registered

- **WHEN** a bundle arrives and several projections are registered
- **THEN** each is offered the bundle and each receives its own relevant subset

#### Scenario: A projection that forgets deleted tenants meets a deletion fact

- **WHEN** a bundle holds a tenant-deletion fact, and a projection that declares it forgets deleted
  tenants declares no handler for it
- **THEN** the fact is still passed to it

#### Scenario: A bundle holds an event no projection handles, of a type the host never registered

- **WHEN** a bundle arrives holding an event that no projection in the host handles, whose type the
  host never registered, beside events a projection does handle
- **THEN** the handled events are applied, the bundle is treated as processed, and the unhandled
  event's payload is never decrypted

#### Scenario: A replay meets such an event

- **WHEN** a replay rebuilds over a store that holds an event no projection in the host handles, of a
  type the host never registered
- **THEN** the replay completes

#### Scenario: A store reader meets such an event

- **WHEN** a projection reads the store on the Orleans execution model, and its partition holds such an
  event
- **THEN** the reader reads past it rather than stalling the partition — verified on the in-process
  execution-model host over SQLite

#### Scenario: An event is upcast into a type a projection handles

- **WHEN** a recorded event's type is handled by no projection, and an upcaster turns it into a type a
  projection handles
- **THEN** the upcast event is dispatched to that projection

#### Scenario: An unreadable event carries the name of a handled type

- **WHEN** an event's type does not resolve, and its name without namespace or assembly is the name of
  a type a projection in the host handles
- **THEN** the bundle, the replay batch or the reader fails, naming the type, as it does for any
  unregistered type
