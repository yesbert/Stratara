## MODIFIED Requirements

### Requirement: A projection declares the events it cares about by handling them

A projection SHALL be dispatched only the events it declares handlers for, determined from the
handler signatures themselves rather than from a separate registration. The one exception: a
projection that declares it forgets deleted tenants SHALL also be dispatched the framework's
tenant-deletion facts, as *A projection can forget a deleted tenant* describes.

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

### Requirement: A projection can report that a fact's prerequisite has not been applied yet

The framework SHALL offer a projection a way to say that the entity a fact refers to does not exist
in its read model yet, distinct from any other failure. A bundle reported that way SHALL be retried
within the process a bounded number of times with a short backoff, holding no aggregate lock while it
waits, and SHALL fail as an unhandled projection failure only once those retries are exhausted. The
one exception: where the projection declares it forgets deleted tenants and has recorded the fact's
owning tenant as deleted, the fact SHALL be passed over, as *A projection can forget a deleted
tenant* describes.

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

## ADDED Requirements

### Requirement: A projection can forget a deleted tenant

The framework SHALL let a projection declare that it forgets a tenant once the tenant is deleted. A
projection that does not declare it SHALL behave exactly as it would without this requirement.

Declaring it is the projection's promise that, once either of the framework's tenant-deletion facts
has been applied, the deleted tenant's data is gone from its read model: a tenant's deletion, and the
cascade that deletes a customer's tenants. A projection that keeps a tenant's data after either SHALL
NOT declare it.

For a projection that declares it:

- The framework SHALL hand the projection both tenant-deletion facts, whether or not the projection
  handles them itself. After the projection has applied one, the framework SHALL record, for that
  projection, every tenant the fact deleted.
- The record SHALL be kept for each projection separately, in the order that projection applies
  facts. A projection's record SHALL NOT depend on how far any other projection has got.
- Where the projection reports that a fact's prerequisite has not been applied yet, and the fact's
  owning tenant is recorded as deleted for that projection, the framework SHALL pass the fact over. It
  SHALL treat the fact as applied, SHALL NOT retry it, and SHALL log it with the projection, the
  stream, the fact's type and the tenant.
- A fact of a recorded tenant that the projection applies without such a report SHALL be applied as
  usual. A report for any tenant not recorded SHALL be retried and SHALL fail as the missing-prerequisite
  requirement describes. So SHALL a report the record could not be consulted for.
- The record is derived from the facts. A replay SHALL empty the record of each declaring projection
  the host registers, before it empties the read models. It SHALL NOT touch the record of a
  projection the host does not register. A projection rebuilt on its own SHALL have its own record
  emptied just before its read model. The record is then rebuilt from the history, in order.
- Where no place to keep the record is registered in the host, the projection SHALL fail on the first
  fact it is handed, with a message naming the registration. It SHALL NOT go on without the record.

A host without a declaring projection SHALL NOT need the place that keeps the record, for a replay or
for anything else.

A deletion removes a tenant's data from a projection on purpose. A fact recorded for that tenant
afterwards is legitimate history: work queued before the deletion ran to its end. The projection
meets it with nothing to apply it to. The missing-prerequisite report can tell "not yet" from "never"
only by waiting, so it turns such a fact into a dead-lettered bundle live and into a failed replay on
rebuild. A replay that fails leaves the read models it emptied empty. Knowing which tenants the
projection itself has deleted is what separates "removed on purpose" from both.

The knowledge is kept per projection because projections apply facts in parallel, and on a
store-reading host each at its own pace. A shared record could tell one projection about a deletion
it has not applied yet. A record is emptied before the read model rather than after it for two
reasons. A deletion applied in between is then recorded again rather than lost, while a record that
outlives its reset only concerns a tenant whose data is removed at the deletion in any case. And a
missing place to keep it then fails the replay before anything has been emptied.

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

#### Scenario: The cascade deletes several tenants

- **WHEN** a declaring projection applies the cascade that deletes a customer's tenants
- **THEN** every tenant it lists is recorded as deleted for that projection

#### Scenario: The projection does not handle the deletion facts itself

- **WHEN** a declaring projection declares no handler for a deletion fact
- **THEN** it is still handed the fact, nothing of its own runs for it, and the deleted tenants are
  recorded

#### Scenario: A late fact of a deleted tenant is applied without complaint

- **WHEN** a declaring projection applies a fact owned by a recorded tenant without reporting a
  missing prerequisite
- **THEN** the fact is applied as usual

#### Scenario: A missing prerequisite for a tenant that was not deleted

- **WHEN** a declaring projection reports a missing prerequisite for a fact whose owning tenant is not
  recorded as deleted for it
- **THEN** the bundle is retried and fails as the missing-prerequisite requirement describes

#### Scenario: The record cannot be consulted

- **WHEN** a declaring projection reports a missing prerequisite and the place that keeps the record
  fails when asked
- **THEN** the report stands as a missing prerequisite, carrying that failure, and is retried as the
  missing-prerequisite requirement describes

#### Scenario: Another projection has already applied the deletion

- **WHEN** one declaring projection has applied a tenant's deletion and another has not reached it yet
- **THEN** the second one's missing-prerequisite reports for that tenant are retried and fail as
  before, until it has applied the deletion itself

#### Scenario: A projection does not declare itself

- **WHEN** a projection that does not declare it reports a missing prerequisite for a fact of a deleted
  tenant
- **THEN** the bundle is retried and fails as the missing-prerequisite requirement describes, and the
  deletion facts reach it only if it handles them

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

#### Scenario: The record is kept apart for each projection

- **WHEN** two projections record the same tenant as deleted, and one of them has its record emptied
- **THEN** the other still holds the tenant, and a tenant recorded twice for one projection is held
  once — verified on SQLite

#### Scenario: No place to keep the record is registered

- **WHEN** a declaring projection is handed a fact in a host where nothing keeps the record
- **THEN** the projection fails with a message naming the registration that provides it
