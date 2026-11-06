# Spec Delta

## MODIFIED Requirements

### Requirement: A projection declares the events it cares about by handling them

A projection SHALL be dispatched only the events it declares handlers for, determined from the
handler signatures themselves rather than from a separate registration. The one exception: a
projection that declares it forgets deleted tenants SHALL also be dispatched the framework's
tenant-deletion facts, as *A declaring projection records the tenants it has seen deleted* describes.

An event that no projection in the host is dispatched SHALL NOT be read. Its type SHALL NOT be resolved
and its payload SHALL NOT be decrypted, so it SHALL NOT need to be registered in the host. It SHALL
fail neither the bundle it arrives in, nor a replay, nor a store reader. Whether a projection is
dispatched an event SHALL be decided on the event's type as it stands after upcasting. An event whose
type does not resolve, but whose name without namespace or assembly is the name of a type a projection
in the host is dispatched, SHALL be read, and SHALL fail as an unregistered type fails. Where the host
replaces how recorded events are mapped, every event SHALL be read as before; where it replaces the
component that dispatches to projections, that component SHALL receive every event of the bundle.

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

#### Scenario: The host replaces the projection dispatch

- **WHEN** a host registers its own component in place of the framework's projection dispatch
- **THEN** it receives every event of the bundle, as before

#### Scenario: An unreadable event carries the name of a handled type

- **WHEN** an event's type does not resolve, and its name without namespace or assembly is the name of
  a type a projection in the host handles
- **THEN** the bundle, the replay batch or the reader fails, naming the type, as it does for any
  unregistered type

### Requirement: A projection can report that a fact's prerequisite has not been applied yet

The framework SHALL offer a projection a way to say that the entity a fact refers to does not exist
in its read model yet, distinct from any other failure. A bundle reported that way SHALL be retried
within the process a bounded number of times with a short backoff, holding no aggregate lock while it
waits, and SHALL fail as an unhandled projection failure only once those retries are exhausted. The
one exception: where the projection declares it forgets deleted tenants and has recorded the fact's
owning tenant as deleted, the fact SHALL be passed over, as *A missing prerequisite of a deleted
tenant's fact is not retried* describes.

The distinction is the point: "the beginning has not arrived" and "the beginning will never arrive"
produce the same observation in a projection, and only time tells them apart. A short wait resolves
the first without turning the second into a poison message.

#### Scenario: The prerequisite arrives during the wait

- **WHEN** a projection reports a missing prerequisite, and the fact that creates the entity is
  applied before the retries are exhausted
- **THEN** the bundle is applied on a later attempt and treated as processed

#### Scenario: The prerequisite never arrives

- **WHEN** a projection reports a missing prerequisite on every attempt
- **THEN** the bundle fails as an unhandled projection failure does, and the failure is recorded with
  the stream and the event type the projection named

#### Scenario: A projection fails for any other reason

- **WHEN** a projection throws anything other than the missing-prerequisite report
- **THEN** the bundle is not retried and fails on the first occurrence, as before

#### Scenario: A waiting bundle does not block the fact it waits for

- **WHEN** a bundle reports a missing prerequisite and the creating fact for the same aggregate is
  handed to another consumer of the same process
- **THEN** the creating fact is applied while the first bundle waits, rather than waiting behind it

## REMOVED Requirements

### Requirement: A projection can forget a deleted tenant

**Reason**: Replaced by the six requirements below rather than edited in place, one behaviour each. The old requirement promised that a fact of a recorded tenant applied without a report is applied as usual, and one of its scenarios said so by name; the replacement reverses that, so the name could not stay. Every other scenario is carried over, and its rationale is kept whole in this change's design.

**Migration**: A declaring projection is no longer called for a fact of a tenant it has recorded as deleted, other than the tenant-deletion facts. A projection that must apply such a fact does not declare the interface.

## ADDED Requirements

### Requirement: A projection can declare that it forgets a deleted tenant

The framework SHALL let a projection declare that it forgets a tenant once the tenant is deleted. A projection that does not declare it SHALL behave exactly as it would without these requirements. Declaring it is the projection's promise that, once either tenant-deletion fact has been applied, the deleted tenant's data is gone from its read model: a tenant's deletion, and the cascade that deletes a customer's tenants. A projection that keeps a tenant's data after either SHALL NOT declare it.

#### Scenario: A projection does not declare itself

- **WHEN** a projection that does not declare it reports a missing prerequisite for a fact of a deleted
  tenant
- **THEN** the bundle is retried and fails as the missing-prerequisite requirement describes, and the
  deletion facts reach it only if it handles them

#### Scenario: A projection that does not declare itself meets a deleted tenant's fact

- **WHEN** a projection that does not declare it is handed a fact owned by a deleted tenant
- **THEN** it is called for the fact as usual, and the record is neither consulted nor needed

### Requirement: A declaring projection records the tenants it has seen deleted

The framework SHALL hand a declaring projection both tenant-deletion facts, whether or not it handles them, and after it has applied one SHALL record, for that projection, every tenant the fact deleted. The record SHALL be kept for each projection separately, in the order it applies facts, and SHALL NOT depend on how far another projection has got: projections apply facts in parallel, so a shared record could tell one about a deletion it has not applied yet.

#### Scenario: The cascade deletes several tenants

- **WHEN** a declaring projection applies the cascade that deletes a customer's tenants
- **THEN** every tenant it lists is recorded as deleted for that projection

#### Scenario: The projection does not handle the deletion facts itself

- **WHEN** a declaring projection declares no handler for a deletion fact
- **THEN** it is still handed the fact, nothing of its own runs for it, and the deleted tenants are
  recorded

#### Scenario: A deletion fact arrives for a tenant already recorded

- **WHEN** a declaring projection is handed a tenant-deletion fact, and the tenant that owns the fact
  or a tenant it deletes is already recorded
- **THEN** the fact is handled as usual and every tenant it deletes is recorded

#### Scenario: The record is kept apart for each projection

- **WHEN** two projections record the same tenant as deleted, and one of them has its record emptied
- **THEN** the other still holds the tenant, and a tenant recorded twice for one projection is held
  once — verified on SQLite

### Requirement: A deleted tenant's facts are passed over before handling

Before handing a declaring projection any fact other than a tenant-deletion fact, the framework SHALL consult its record, and SHALL pass a fact of a recorded tenant over without calling the projection, logged with the projection, the stream, the fact's type and the tenant. A fact with an empty tenant SHALL be handled without consulting it. No answer SHALL be reused for a later fact. A row-adding fact reports nothing, so a record asked only after a report leaves that row for good.

#### Scenario: A late fact of a deleted tenant reports nothing

- **WHEN** a declaring projection is handed a fact owned by a recorded tenant that it would have
  applied without reporting a missing prerequisite
- **THEN** the fact is not applied: the projection is not called for it, and it is passed over and
  logged like any other fact of a recorded tenant

#### Scenario: A fact that would add a row arrives after its tenant's deletion

- **WHEN** a declaring projection has applied a tenant's deletion and is handed a fact owned by that
  tenant that would add a row
- **THEN** the projection is not called for it, no row is added, and the pass-over is logged with the
  projection, the stream, the fact's type and the tenant

#### Scenario: A replay applies a deleted tenant's fact after the deletion

- **WHEN** a replay rebuilds a declaring projection over a history in which a tenant's deletion is
  applied before a row-adding fact owned by that tenant
- **THEN** the replay completes and the read model holds no row of that tenant — verified with the
  replay worker and the default projection handler

#### Scenario: A fact has an empty tenant

- **WHEN** a declaring projection is handed a fact with an empty tenant
- **THEN** the record is not consulted and the fact is handled as usual

#### Scenario: A fact of a deleted tenant would remove something elsewhere

- **WHEN** a declaring projection is handed a fact owned by a recorded tenant whose handler would
  remove something that belongs to another tenant
- **THEN** the fact is passed over like any other, and what it would have removed stays

#### Scenario: Each fact consults the record

- **WHEN** a declaring projection is handed several facts of one tenant that is not recorded
- **THEN** the record is consulted once for each of them, and a deletion applied between two of them
  is seen by the later one

### Requirement: A missing prerequisite of a deleted tenant's fact is not retried

Where a declaring projection reports that a fact's prerequisite has not been applied yet and the fact's owning tenant is recorded, the framework SHALL treat the fact as applied, SHALL NOT retry it, and SHALL log it. A report for a tenant not recorded SHALL be retried and fail as the missing-prerequisite requirement describes. So SHALL a fact the record could not be consulted for, neither applied nor passed over on that attempt. A report tells "not yet" from "never" only by waiting.

#### Scenario: A late fact of a deleted tenant reports a missing prerequisite

- **WHEN** a declaring projection has applied the deletion of a tenant, and later reports a missing
  prerequisite for a fact owned by that tenant
- **THEN** the fact is passed over without being retried, the rest of the bundle or batch is applied,
  and the pass-over is logged with the projection, the stream, the fact's type and the tenant

#### Scenario: A replay meets a fact recorded after its tenant's deletion

- **WHEN** a replay rebuilds a declaring projection over a history in which a tenant is deleted and a
  fact owned by that tenant is recorded afterwards
- **THEN** the replay completes, and the read model does not hold the deleted tenant's data — verified
  with the replay worker and the default projection handler

#### Scenario: A store-reading projection meets a fact recorded after its tenant's deletion

- **WHEN** a declaring projection reads the store on the Orleans execution model, and its partition
  holds a tenant's creation, the cascade that deletes the tenant, and a later fact owned by the tenant
- **THEN** the reader reads past the late fact rather than stalling the partition — verified on the
  in-process execution-model host over SQLite

#### Scenario: A missing prerequisite for a tenant that was not deleted

- **WHEN** a declaring projection reports a missing prerequisite for a fact whose owning tenant is not
  recorded as deleted for it
- **THEN** the bundle is retried and fails as the missing-prerequisite requirement describes

#### Scenario: The record cannot be consulted

- **WHEN** a declaring projection reports a missing prerequisite, or is about to be handed a fact, and
  the place that keeps the record fails when asked
- **THEN** a missing prerequisite is reported for the fact, carrying that failure, the projection is
  not called for it on that attempt, and it is retried as the missing-prerequisite requirement describes

#### Scenario: Another projection has already applied the deletion

- **WHEN** one declaring projection has applied a tenant's deletion and another has not reached it yet
- **THEN** the second one's missing-prerequisite reports for that tenant are retried and fail as
  before, until it has applied the deletion itself

### Requirement: The record of deleted tenants is emptied with the read model

The record is derived from the facts. A replay SHALL empty the record of each declaring projection the host registers, before it empties the read models, and SHALL NOT touch the record of a projection the host does not register. A projection rebuilt on its own SHALL have its own record emptied just before its read model. The record is then rebuilt from the history, in order, and a deletion applied in between is recorded again rather than lost.

#### Scenario: A replay starts

- **WHEN** a replay starts in a host with declaring projections
- **THEN** each one's record is emptied before the read models are emptied

#### Scenario: Another deployment shares the read store

- **WHEN** a host replays while another deployment's projections keep their records in the same read
  store
- **THEN** only the records of the replaying host's own declaring projections are emptied

#### Scenario: A host without a declaring projection replays

- **WHEN** a host that registers no declaring projection replays
- **THEN** the place that keeps the record is not touched, and need not exist

#### Scenario: One projection is rebuilt on the Orleans execution model

- **WHEN** a rebuildable, declaring projection is rebuilt on its own
- **THEN** its own record is emptied just before its read model, and every other projection's record
  is kept

### Requirement: A declaring projection needs a place to keep its record

Where no place to keep the record is registered in the host, a declaring projection SHALL fail on the first fact it is handed, with a message naming the registration, and SHALL NOT go on without the record. A replay SHALL fail on the same absence before it has emptied anything. A host without a declaring projection SHALL NOT need that place, for a replay or for anything else.

#### Scenario: No place to keep the record is registered

- **WHEN** a declaring projection is handed a fact in a host where nothing keeps the record
- **THEN** the projection fails with a message naming the registration that provides it

### Requirement: A host's truncator leaves the saga positions in place

The contract of `IProjectionViewTruncator` SHALL state that a truncator empties read models and that the checkpoints of the store-reading sagas SHALL survive it, since a saga without its position runs its side effects again. It SHALL NOT recommend emptying the read store's whole schema. A saga has no replay, so a rebuild of the read side is no reason to move one.

#### Scenario: A consumer writes a truncator

- **WHEN** a consumer reads the contract of `IProjectionViewTruncator` or the guide to writing a projection
- **THEN** it is told which checkpoints must survive a truncation and why, and is not advised to cascade across the schema
