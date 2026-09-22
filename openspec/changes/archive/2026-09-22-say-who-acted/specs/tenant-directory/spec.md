## MODIFIED Requirements

### Requirement: Membership is an authorization source in its own right

The framework SHALL offer role checking against tenant-scoped membership roles, and a variant that
additionally consults the user's global roles when membership yields no answer. Both SHALL fail
closed.

A role SHALL be looked up in the tenant the request concerns. In addition, a host SHALL be able to
name roles that are looked up in the tenant the actor is a member of instead, for an actor whose
membership is in another tenant than the one it acts on — a machine actor holding one membership
and serving many tenants, or an operator with no membership in the tenant being administered. The
set SHALL be empty unless a host configures it, and SHALL admit only the roles named in it: a role
the actor holds in its own tenant and that the host has not named SHALL NOT pass a check made
against another tenant. The documentation SHALL state, for each kind of actor, in which tenant its
roles are looked up.

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

#### Scenario: A named role is held in the actor's own tenant

- **WHEN** the actor's tenant differs from the subject tenant, the actor holds no membership in the
  subject tenant, the host has named the role, and the actor's active membership in its own tenant
  carries it
- **THEN** the check passes

#### Scenario: An unnamed role is held in the actor's own tenant

- **WHEN** the same actor is checked for a role it holds in its own tenant that the host has not
  named
- **THEN** the check fails, so that configuring one role does not carry the actor's other roles into
  a tenant it is not a member of

#### Scenario: No role is named

- **WHEN** a host names no such role
- **THEN** every check resolves exactly as it did before, in the subject tenant and then, for the
  variant that does so, in the global role store

### Requirement: Cross-tenant access can be authorized from stored facts

The framework SHALL offer a cross-tenant authorizer that permits an operation when the actor holds
an active membership in the subject tenant, or holds one of a configured set of platform roles.
Without configuration it SHALL permit nothing.

A configured platform role SHALL be recognised where the actor holds it, whether that is a global
role or a role of the actor's own membership. An actor whose only role level is its membership —
a machine actor, which holds no global roles — SHALL therefore be able to pass by a configured
role, as this requirement has always stated.

#### Scenario: The actor is a member of the subject tenant

- **WHEN** a cross-tenant operation's actor holds an active membership in the subject tenant
- **THEN** it is permitted

#### Scenario: The actor holds a configured platform role

- **WHEN** the actor holds one of the configured cross-tenant roles
- **THEN** it is permitted without any membership in the subject tenant

#### Scenario: The configured role is held only through the actor's own membership

- **WHEN** the actor holds a configured cross-tenant role through its membership in its own tenant,
  holds no global roles, and holds no membership in the subject tenant
- **THEN** it is permitted

#### Scenario: Neither applies

- **WHEN** the actor has no membership in the subject tenant, only a pending one, or no configured
  role
- **THEN** it is refused
