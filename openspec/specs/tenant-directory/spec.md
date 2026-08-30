# tenant-directory Specification

## Purpose
Record which users belong to which tenants, with what roles and in what state, so that every
tenant-aware decision in the system — which tenant a request operates on, what a principal may do
there, whose data an erasure request covers — reads one answer instead of deriving its own.

## Requirements

### Requirement: A user may belong to several tenants, with roles per membership

Membership SHALL be many-to-many: one user may belong to several tenants and one tenant may have
many users. Roles SHALL be recorded per membership, so a user can hold different roles in different
tenants.

Roles held per membership are a separate level from any global roles a user holds; the two SHALL NOT
be merged in the directory.

#### Scenario: A user belongs to two tenants with different roles

- **WHEN** a user holds memberships in two tenants
- **THEN** each membership carries its own roles, and reading one does not reveal or apply the other's

#### Scenario: Membership is looked up in both directions

- **WHEN** memberships are queried by user and by tenant
- **THEN** both views report the same relationships

#### Scenario: A user has no membership in a tenant

- **WHEN** a membership is requested for a user and tenant with no relationship
- **THEN** nothing is returned, and the corresponding list view is empty rather than absent

### Requirement: An invited membership confers nothing

A membership SHALL carry a status distinguishing an active member from an invited one. A pending
membership SHALL confer no roles and SHALL NOT count as membership anywhere access is decided.

#### Scenario: A membership is pending

- **WHEN** a membership exists with pending status
- **THEN** it confers no roles, does not permit access to the tenant, and does not satisfy any check
  that requires membership

#### Scenario: A pending membership is activated

- **WHEN** a pending membership is set to active
- **THEN** its roles take effect

### Requirement: Writing a membership replaces it wholly

Setting a membership SHALL insert it where none exists and replace the roles and status where one
does, rather than merging with what was there.

#### Scenario: A membership is written twice

- **WHEN** a membership is written for a user and tenant that already has one
- **THEN** the stored roles and status are exactly those written, and previously held roles that
  were not written are gone

### Requirement: A user has at most one selected tenant, and it must be a live membership

A user SHALL have at most one persisted active-tenant selection. Selecting a tenant SHALL require an
active membership in it, and SHALL replace any previous selection rather than adding to it.

#### Scenario: A tenant is selected

- **WHEN** a user with an active membership selects that tenant
- **THEN** the selection is recorded

#### Scenario: A tenant is selected without an active membership

- **WHEN** a user attempts to select a tenant in which they have no membership, or only a pending
  one
- **THEN** the selection is refused

#### Scenario: A different tenant is selected

- **WHEN** a user with an existing selection selects another tenant they are an active member of
- **THEN** the single stored selection is updated rather than a second one being added

#### Scenario: The selected membership is removed

- **WHEN** the membership a user's selection points at is removed
- **THEN** the selection is cleared, so it can never point at a membership that no longer exists

### Requirement: Erasure sweeps remove a subject's whole footprint

The directory SHALL support removing every membership of one user across all tenants, and every
membership of one tenant across all users. Each sweep SHALL also remove the active-tenant selections
it invalidates.

These are the directory's part of an erasure obligation; leaving a dangling selection or an
orphaned membership behind would leave the subject partially present.

#### Scenario: A user is erased

- **WHEN** every membership of a user is removed
- **THEN** their memberships in all tenants are gone, and their active-tenant selection with them

#### Scenario: A tenant is removed

- **WHEN** every membership of a tenant is removed
- **THEN** all its members' memberships are gone, along with any active-tenant selection that
  pointed at it

### Requirement: The tenant a request operates on is derived from membership at sign-in

The framework SHALL derive a principal's tenant from membership and expose it as the
`stratara:tenant_id` claim, resolving in this order: the user's persisted active-tenant selection,
then their only active membership where they have exactly one, and otherwise no claim at all.

Emitting no claim is the fail-closed outcome: it resolves to the reserved default tenant rather than
to an arbitrary one of several.

#### Scenario: The user has a persisted selection

- **WHEN** a principal is built for a user with an active-tenant selection
- **THEN** the claim carries the selected tenant

#### Scenario: The user has exactly one active membership and no selection

- **WHEN** a principal is built for a user with one active membership and no selection
- **THEN** the claim carries that tenant

#### Scenario: The user has several memberships and no selection

- **WHEN** a principal is built for a user with several active memberships and no selection
- **THEN** the resolution is deterministic rather than arbitrary, and where it cannot be determined
  no claim is emitted

#### Scenario: The user has no active membership

- **WHEN** a principal is built for a user with no active membership
- **THEN** no tenant claim is emitted

#### Scenario: A tenant claim is already present

- **WHEN** the principal already carries a tenant claim
- **THEN** it is left untouched

### Requirement: The tenant claim can be stamped at sign-in or resolved per request

The framework SHALL offer both: stamping the claim when the principal is issued, and resolving it
live on each request. With live resolution, a change of selected tenant SHALL take effect without
the user signing in again.

#### Scenario: The claim is resolved per request

- **WHEN** live resolution is configured and a user changes their selected tenant
- **THEN** the next request carries the new tenant without a new sign-in

#### Scenario: Live resolution runs repeatedly

- **WHEN** the same principal is transformed more than once
- **THEN** the result is the same and no duplicate claim accumulates

#### Scenario: The principal is not authenticated

- **WHEN** live resolution encounters an unauthenticated principal
- **THEN** it does nothing

### Requirement: Membership is an authorization source in its own right

The framework SHALL offer role checking against tenant-scoped membership roles, and a variant that
additionally consults the user's global roles when membership yields no answer. Both SHALL fail
closed.

#### Scenario: The role is held through membership

- **WHEN** a role check is made and the user's active membership in the subject tenant carries it
- **THEN** it passes, without consulting any global role store

#### Scenario: Membership does not carry the role

- **WHEN** membership does not carry the role and the global-role variant is in use
- **THEN** the user's global roles are consulted

#### Scenario: Neither carries the role

- **WHEN** neither membership nor global roles carry it
- **THEN** the check fails

#### Scenario: The user is unknown or has no session

- **WHEN** no session is set, or the user is unknown to the identity store
- **THEN** the check fails rather than defaulting to permitted

### Requirement: Cross-tenant access can be authorized from stored facts

The framework SHALL offer a cross-tenant authorizer that permits an operation when the actor holds
an active membership in the subject tenant, or holds one of a configured set of platform roles.
Without configuration it SHALL permit nothing.

#### Scenario: The actor is a member of the subject tenant

- **WHEN** a cross-tenant operation's actor holds an active membership in the subject tenant
- **THEN** it is permitted

#### Scenario: The actor holds a configured platform role

- **WHEN** the actor holds one of the configured cross-tenant roles
- **THEN** it is permitted without any membership in the subject tenant

#### Scenario: Neither applies

- **WHEN** the actor has no membership in the subject tenant, only a pending one, or no configured
  role
- **THEN** it is refused

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

### Requirement: The directory tables can be hosted in an existing context

A consumer SHALL be able to add the directory's tables to a database context it already has, so the
directory does not force a second migration lineage.

The consumer SHALL be able to choose how the directory's stores obtain that context: shared for the
whole request, or a fresh one for each operation. The framework SHALL state, where each choice is
declared, what the choice costs — that a shared context permits only one operation at a time and
that a store's write on it also commits whatever the consumer has left unsaved on that context, and
that a context per operation has neither property but places a store's write outside any transaction
the consumer opened on their own context.

Sharing the request's context remains the default, so a consumer who chooses nothing keeps the
behaviour they have.

#### Scenario: A consumer hosts the directory in its own context

- **WHEN** a consumer applies the directory's model to an existing context
- **THEN** the directory tables are part of that context's model and migrations

#### Scenario: Directory work is issued concurrently within one request

- **WHEN** a consumer has chosen a context per operation and issues two directory operations at the
  same time within one request
- **THEN** both complete, rather than one failing because the other holds the context

#### Scenario: A consumer changes nothing

- **WHEN** a consumer registers the directory's stores as before
- **THEN** the stores share the request's context exactly as they did, and the constraint that
  follows from sharing it is stated where that registration is declared

### Requirement: A subject's erasure can be performed as one operation

The framework SHALL offer a single operation that erases a subject across every plane it holds data
in — membership, key material, settings and API keys — in an order that leaves nothing unreachable
before it has been removed.

The operation SHALL report what it covered, and the framework SHALL state what it does not cover, so
a consumer can tell what remains their own responsibility.

#### Scenario: A user is erased

- **WHEN** a user's erasure is performed
- **THEN** their memberships, their active-tenant selection, their settings across all tenants, the
  API keys bound to them and their key material are all removed, and data encrypted under their keys
  becomes unrecoverable

#### Scenario: A tenant is erased

- **WHEN** a tenant's erasure is performed
- **THEN** its memberships, its machine keys and their materialised memberships, its settings across
  all users and its key material are all removed

#### Scenario: A consumer asks what erasure covers

- **WHEN** a consumer needs to know whether an erasure is complete
- **THEN** the framework states which planes it covers and which it does not — in particular that
  read models a consumer's own projections built, and any unprotected data in the event stream, are
  the consumer's to handle

#### Scenario: A plane's sweep fails partway

- **WHEN** one plane's sweep fails during a composed erasure
- **THEN** the failure identifies which plane it was, so the operation can be resumed rather than
  restarted blindly
