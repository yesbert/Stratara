## MODIFIED Requirements

### Requirement: Tenants themselves are event-sourced

The framework SHALL ship a tenant aggregate whose lifecycle — creation, renaming, activation,
deactivation, assignment to a customer, locale change and deletion — is recorded as events, with a
read model projected from them.

A tenant's creation event SHALL declare the tenant it creates as that event's owning tenant, so that
the recorded owner of a new tenant is the tenant itself and does not depend on which session
performed the creation.

#### Scenario: A tenant's lifecycle is recorded

- **WHEN** a tenant is created, renamed, activated, deactivated, assigned, has its default locale
  changed, or is deleted
- **THEN** each is a distinct recorded event and the read model reflects it

#### Scenario: A tenant is created from a session belonging to another tenant

- **WHEN** a tenant is created while the acting session's data-owner tenant is a different tenant
- **THEN** the creation event is owned by the tenant being created, not by the session's tenant

#### Scenario: A lifecycle event arrives for a tenant not in the read model

- **WHEN** an event other than creation arrives for a tenant the read model does not hold
- **THEN** it is ignored rather than failing the bundle

#### Scenario: A creation event is delivered twice

- **WHEN** a tenant-created event is processed for a tenant already present
- **THEN** nothing is written a second time

#### Scenario: A deletion races another writer

- **WHEN** a deletion is processed for a row another writer has already removed
- **THEN** the concurrency failure is absorbed rather than failing the bundle
