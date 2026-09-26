## ADDED Requirements

### Requirement: A projection can forget a deleted tenant

The framework SHALL let a projection declare that it forgets a tenant once the tenant is deleted. A
projection that does not declare it SHALL behave exactly as it would without this requirement.

For a projection that declares it:

- The framework SHALL hand the projection the tenant-deletion facts the framework ships, whether or
  not the projection handles them itself. These are a tenant's deletion and the cascade that deletes
  a customer's tenants. After the projection has applied one of them, the framework SHALL record, for
  that projection, every tenant the fact deleted.
- The record SHALL be kept for each projection separately, in the order that projection applies
  facts. A projection's record SHALL NOT depend on how far any other projection has got.
- Where the projection reports that a fact's prerequisite has not been applied yet, and the fact's
  owning tenant is recorded as deleted for that projection, the framework SHALL pass the fact over. It
  SHALL treat the fact as applied, SHALL NOT retry it, and SHALL log it with the projection, the
  stream, the fact's type and the tenant.
- A fact of a recorded tenant that the projection applies without such a report SHALL be applied as
  usual. A report for any tenant not recorded SHALL be retried and SHALL fail as the missing-prerequisite
  requirement describes.
- The record is derived from the facts. A replay SHALL empty it together with the read models, and a
  projection rebuilt on its own SHALL have its own record emptied with its read model, so the record
  is rebuilt from the history in order.
- Where no place to keep the record is registered in the host, the projection SHALL fail on the first
  fact it is handed, with a message naming the registration. It SHALL NOT go on without the record.

A deletion removes a tenant's data from a projection on purpose. A fact recorded for that tenant
afterwards is legitimate history: work queued before the deletion ran to its end. The projection
meets it with nothing to apply it to. The missing-prerequisite report can tell "not yet" from "never"
only by waiting, so it turns such a fact into a dead-lettered bundle live and into a failed replay on
rebuild. A replay that fails leaves the read models it emptied empty. Knowing which tenants the
projection itself has deleted is what separates "removed on purpose" from both. The knowledge is kept
per projection because projections apply facts in parallel, and on a store-reading host each at its
own pace. A shared record could tell one projection about a deletion it has not applied yet.

#### Scenario: A late fact of a deleted tenant reports a missing prerequisite

- **WHEN** a declaring projection has applied the deletion of a tenant, and later reports a missing
  prerequisite for a fact owned by that tenant
- **THEN** the fact is passed over without being retried, the rest of the bundle or batch is applied,
  and the pass-over is logged with the projection, the stream, the fact's type and the tenant

#### Scenario: A replay meets a fact recorded after its tenant's deletion

- **WHEN** a replay rebuilds a declaring projection over a history in which a tenant is deleted and a
  fact owned by that tenant is recorded afterwards
- **THEN** the replay completes, and the read model does not hold the deleted tenant's data

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

- **WHEN** a replay empties the read models
- **THEN** every projection's record of deleted tenants is emptied with them

#### Scenario: One projection is rebuilt on the Orleans execution model

- **WHEN** a rebuildable, declaring projection is rebuilt on its own
- **THEN** its own record is emptied with its read model, and every other projection's record is kept

#### Scenario: No place to keep the record is registered

- **WHEN** a declaring projection is handed a fact in a host where nothing keeps the record
- **THEN** the projection fails with a message naming the registration that provides it
