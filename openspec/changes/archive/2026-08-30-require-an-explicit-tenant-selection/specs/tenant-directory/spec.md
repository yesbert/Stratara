## MODIFIED Requirements

### Requirement: The tenant a request operates on is derived from membership at sign-in

The framework SHALL derive a principal's tenant from membership and expose it as the
`stratara:tenant_id` claim, resolving in this order: the user's persisted active-tenant selection,
then their only active membership where they have exactly one, and otherwise no claim at all.

Emitting no claim is the fail-closed outcome: it resolves to the reserved default tenant rather than
to an arbitrary one of several.

Where a user has several active memberships and no valid selection, the framework SHALL NOT choose
one for them. Belonging to several tenants is supported; deciding which of them a request acts in is
the user's, and the host must obtain that decision before the user can act. A wrong tenant claim
fails invisibly — the work lands in the wrong tenant and nothing reports it — where a missing one
fails closed and visibly.

#### Scenario: The user has a persisted selection

- **WHEN** a principal is built for a user with an active-tenant selection
- **THEN** the claim carries the selected tenant

#### Scenario: The user has exactly one active membership and no selection

- **WHEN** a principal is built for a user with one active membership and no selection
- **THEN** the claim carries that tenant

#### Scenario: The user has several memberships and no selection

- **WHEN** a principal is built for a user with several active memberships and no valid selection
- **THEN** no tenant claim is emitted, whatever order the memberships happen to be in

#### Scenario: A stored selection no longer matches a live membership

- **WHEN** a user's persisted selection names a tenant they are no longer an active member of, and
  they hold several other active memberships
- **THEN** no tenant claim is emitted, rather than one of the remaining memberships being chosen

#### Scenario: The user has no active membership

- **WHEN** a principal is built for a user with no active membership
- **THEN** no tenant claim is emitted

#### Scenario: A tenant claim is already present

- **WHEN** the principal already carries a tenant claim
- **THEN** it is left untouched
