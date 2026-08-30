# tenant-isolation Specification

## Purpose
Stop one tenant's data reaching another tenant, at both places where it could leak — the request
entrance and the database query — while still allowing the deliberate, authorized cross-tenant
operations a platform operator needs.

## Requirements

### Requirement: Tenant isolation is opt-in per request type

The framework SHALL enforce isolation only on requests that declare themselves tenant-scoped by
implementing the tenant-scoped request marker and exposing the tenant they target. Requests that do
not declare it SHALL pass through the guard untouched.

This is deliberate: the framework cannot know which of a consumer's requests carry a tenant, and
guessing would either block legitimate requests or give false assurance.

#### Scenario: A request does not declare a tenant

- **WHEN** a request that does not implement the tenant-scoped marker is dispatched
- **THEN** the isolation guard does not act on it and the handler runs

#### Scenario: A request declares the tenant it targets

- **WHEN** a request implements the tenant-scoped marker
- **THEN** the guard compares the tenant it targets against the session before the handler runs

### Requirement: A tenant-scoped request must target the session's data owner

The guard SHALL compare the request's target tenant against the session's **data-owner** tenant —
not the actor's tenant — and SHALL reject the request when they differ.

Comparing against the actor would defeat the purpose: a privileged operation legitimately has an
actor from a different tenant, and the data owner is what decides which rows the operation may
touch.

#### Scenario: The request targets the session's data owner

- **WHEN** a tenant-scoped request targets the same tenant the session identifies as the data owner
- **THEN** the guard permits it and the handler runs

#### Scenario: The request targets a different tenant

- **WHEN** a tenant-scoped request targets a tenant other than the session's data owner
- **THEN** the request is rejected with a tenant-access-denied failure carrying both the requested
  and the session tenant
- **AND** the handler is never invoked
- **AND** the rejection is recorded at warning level with the request type and both tenants

#### Scenario: The request arrives without a session

- **WHEN** a tenant-scoped request is dispatched and no session context is set
- **THEN** the request is rejected rather than treated as unscoped — absence of identity is not
  permission

### Requirement: Cross-tenant operations are permitted by default and gated in strict mode

The framework SHALL offer two enforcement modes. In the default mode a cross-tenant operation —
one whose session actor tenant differs from its data-owner tenant — SHALL be permitted once the
subject check passes, because the endpoint that promoted the data owner to the target is expected
to have authorized it. In strict mode every such operation SHALL additionally be referred to a
cross-tenant authorizer, and refused unless that authorizer permits it.

#### Scenario: A privileged operation in default mode

- **WHEN** the session's actor tenant differs from its data-owner tenant, the request targets the
  data-owner tenant, and the mode is the default
- **THEN** the request proceeds without any further check

#### Scenario: A same-tenant operation in strict mode

- **WHEN** the session's actor tenant equals its data-owner tenant and the mode is strict
- **THEN** the cross-tenant authorizer is not consulted at all — strict mode adds no cost to the
  ordinary case

#### Scenario: A cross-tenant operation the authorizer permits

- **WHEN** the mode is strict, the operation is cross-tenant, and the registered authorizer permits
  it for that session
- **THEN** the request proceeds
- **AND** the permitted cross-tenant operation is recorded at information level with the actor
  tenant and the data-owner tenant

#### Scenario: A cross-tenant operation the authorizer refuses

- **WHEN** the mode is strict, the operation is cross-tenant, and the authorizer does not permit it
- **THEN** the request is rejected with a tenant-access-denied failure and recorded at warning level

### Requirement: Strict mode denies cross-tenant access until a consumer grants it

The framework SHALL ship a cross-tenant authorizer that permits nothing, registered so that a
consumer's own authorizer replaces it. Enabling strict mode without registering an authorizer
SHALL therefore refuse every cross-tenant operation rather than allowing them.

A security mode whose default is permissive is a mode nobody has actually enabled.

#### Scenario: Strict mode with no authorizer registered

- **WHEN** strict mode is enabled and the consumer has registered no cross-tenant authorizer
- **THEN** every cross-tenant operation is refused

#### Scenario: The consumer registers its own authorizer

- **WHEN** a consumer registers a cross-tenant authorizer
- **THEN** that authorizer decides, and the shipped deny-everything default is not used

### Requirement: A rejected request reaches the caller as a forbidden response

A tenant-isolation rejection SHALL be signalled as a distinct failure type that a consumer's
boundary can map to an HTTP 403, separately from an authentication failure and from a validation
failure.

#### Scenario: A consumer maps the rejection at its boundary

- **WHEN** a tenant-scoped request is rejected by the guard
- **THEN** the failure identifies itself as a tenant-access denial, carries the requested tenant and
  the session tenant, and can be caught without reference to the mediator package

### Requirement: Tenant-scoped rows are filtered at the database as well as at the entrance

The framework SHALL additionally constrain every entity declared tenant-scoped to the ambient
tenant of its database context, so that a read reaching the store without passing the request
guard still cannot return another tenant's rows.

The two layers are independent on purpose: the entrance guard covers requests, and the query filter
covers every query the context issues, including ones the guard never saw.

#### Scenario: A tenant-scoped entity is queried

- **WHEN** an entity type declared tenant-scoped is queried through a tenant-scoped context
- **THEN** only rows whose tenant matches that context's ambient tenant are returned

#### Scenario: An entity type is not declared tenant-scoped

- **WHEN** an entity type does not declare itself tenant-scoped
- **THEN** no filter is installed for it — the framework filters what a consumer marks, and marking
  is the consumer's decision

### Requirement: The guard's position in the pipeline is the consumer's to choose

Tenant isolation SHALL run as a pipeline stage registered independently of validation, so that a
consumer decides whether an invalid request is rejected before or after the tenant check.

#### Scenario: Isolation registered after validation

- **WHEN** validation is registered before tenant isolation
- **THEN** a request that is both invalid and cross-tenant is rejected as invalid first
