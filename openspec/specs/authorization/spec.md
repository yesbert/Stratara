# authorization Specification

## Purpose
Let an application state, on the request type itself, who is allowed to dispatch it — coarsely by
role and finely by permission — and guarantee that a stated requirement is either enforced or the
host refuses to start, never silently ignored.

## Requirements

### Requirement: A request declares its own authorization requirements

Authorization requirements SHALL be declared on the request type with `[RequireRole]` and
`[RequirePermission]` attributes. Multiple attributes of either kind SHALL be combined with AND —
every listed role and every listed permission must be held — and the two kinds SHALL be freely
combinable on the same request.

#### Scenario: A request carries several role requirements

- **WHEN** a request type carries more than one `[RequireRole]` attribute
- **THEN** every one of those roles is checked, and the first one the caller does not hold rejects
  the request

#### Scenario: A request carries several permission requirements

- **WHEN** a request type carries more than one `[RequirePermission]` attribute
- **THEN** the caller must hold all of them

#### Scenario: A request carries both kinds

- **WHEN** a request type carries both a role requirement and a permission requirement
- **THEN** both are enforced

#### Scenario: A request carries neither

- **WHEN** a request type carries no authorization attribute
- **THEN** it dispatches without any authorization lookup being performed at all

### Requirement: Requirements are read from the runtime type

The guard SHALL read authorization attributes from the request instance's runtime type, not from
the static type it was dispatched as, so that dispatching through a base type or an interface does
not bypass a requirement declared on the concrete type.

#### Scenario: A guarded request is dispatched through a base reference

- **WHEN** a request whose concrete type carries a role requirement is dispatched through a
  reference of a less-derived type
- **THEN** the requirement is still enforced

### Requirement: A missing role or permission is refused before the handler runs

Where the caller does not hold a required role or permission, the framework SHALL refuse the
request before the handler is resolved or invoked, and SHALL signal the refusal with a failure that
names the requirement that was missing.

Permission denials SHALL be a specialisation of role denials, so that a consumer catching the
general authorization failure catches both.

#### Scenario: A required role is missing

- **WHEN** the authorization provider reports that the caller is not in a required role
- **THEN** the request is refused with an authorization failure naming that role, and the handler
  never runs

#### Scenario: A required permission is missing

- **WHEN** the caller's effective permissions do not include a required permission
- **THEN** the request is refused with a permission-authorization failure naming that permission
- **AND** that failure is catchable as a general authorization failure, so an existing role-era
  handler maps it without change

### Requirement: Permission evaluation fails closed

Where a permission-guarded request is dispatched and the caller's identity cannot be established,
the framework SHALL refuse the request. Where it is dispatched and no permission resolver is
registered at all, the framework SHALL fail loudly rather than refusing quietly, because that is a
misconfiguration rather than a denial.

#### Scenario: No session identity is available

- **WHEN** a permission-guarded request is dispatched and no session context is set
- **THEN** the request is refused as a permission denial — absence of identity is never treated as
  holding the permission

#### Scenario: No permission resolver is registered

- **WHEN** a permission-guarded request is dispatched and no permission resolver is registered
- **THEN** the framework raises a configuration failure that names the request type and how to
  register a resolver, rather than a denial that would look like a policy decision

### Requirement: Permissions are resolved for the actor within the data owner's tenant

Effective permissions SHALL be resolved for the **actor** who triggered the request, scoped to the
**data-owner** tenant the request concerns. They SHALL NOT be carried in the session context, in a
cookie or in a token.

Resolving per request is what makes a permission change take effect without re-issuing a sign-in;
embedding permissions in a token would make revocation wait for the token to expire.

#### Scenario: Permissions are resolved for a request

- **WHEN** a permission-guarded request is dispatched with a session in place
- **THEN** the effective permission set is resolved for the session's actor user within the
  session's data-owner tenant

#### Scenario: A request is not permission-guarded

- **WHEN** a request carries no permission requirement
- **THEN** the permission resolver is never called

### Requirement: The permission vocabulary is declared in code and is strict

An application SHALL declare its permission names and its role-to-permission grants in a catalog at
start-up. Granting a permission that has not been declared SHALL fail immediately, so that a typo
in a grant surfaces at start-up rather than becoming a permission nobody can ever hold.

#### Scenario: A permission is granted to a role

- **WHEN** a declared permission is granted to a role
- **THEN** that role's grants include it, and further grants to the same role accumulate rather
  than replacing

#### Scenario: An undeclared permission is granted

- **WHEN** a grant names a permission that was never declared
- **THEN** the declaration fails immediately, naming the permission and how to declare it

#### Scenario: A permission is declared twice

- **WHEN** the same permission name is declared more than once
- **THEN** the redeclaration has no effect and does not fail

#### Scenario: A name is empty

- **WHEN** a permission name or a role name is empty or whitespace
- **THEN** it is rejected

### Requirement: A stated requirement is never silently ignored

The framework SHALL refuse to start a host that contains authorization-guarded request types but is
not wired to enforce them. This SHALL cover a host whose mediator does not authorize, and a host
that carries permission-guarded types without a permission resolver.

#### Scenario: A guarded type exists but the mediator does not authorize

- **WHEN** the host loads a request type carrying a role requirement and the registered mediator is
  not an authorizing one
- **THEN** the host fails to start, naming the offending type and how to register an authorizing
  mediator

#### Scenario: Permission-guarded types exist without a resolver

- **WHEN** the host loads a permission-guarded request type, the mediator authorizes, but no
  permission resolver is registered
- **THEN** the host fails to start, naming the offending type and how to register a resolver

#### Scenario: A custom authorizing decorator wraps the built-in one

- **WHEN** a consumer registers its own mediator decorator that delegates to the authorizing
  mediator and declares itself authorizing
- **THEN** the host starts — the check recognises the declaration, not a specific implementation
  type

#### Scenario: No mediator is registered at all

- **WHEN** no mediator is registered
- **THEN** the check does nothing and start-up proceeds

### Requirement: Asynchronous command dispatch is guarded identically

Where a command is dispatched through the outbox rather than in-process, the framework SHALL apply
the same role and permission requirements before the command is enqueued, so that the route a
command takes does not change who may issue it.

#### Scenario: A guarded command is enqueued

- **WHEN** a command carrying a role or permission requirement is dispatched through the outbox
- **THEN** the requirement is checked before the command is enqueued, and a refusal prevents the
  enqueue

#### Scenario: Event bundles pass through unguarded

- **WHEN** event bundles are enqueued through the same dispatcher
- **THEN** no authorization check is applied — bundles are produced by the framework from events
  already written, not dispatched by a caller, so there is no caller to authorize

### Requirement: Catalog permissions are usable as HTTP authorization policies

Every permission declared in the catalog SHALL be usable as an HTTP authorization policy name
without registering it individually. A policy name that is not a declared permission SHALL be left
to the host's own policy resolution.

#### Scenario: An endpoint is guarded by a declared permission name

- **WHEN** an endpoint requires an authorization policy whose name is a declared permission
- **THEN** the request is permitted only if the caller's effective permissions include it

#### Scenario: An endpoint is guarded by a name that is not a permission

- **WHEN** an endpoint requires a policy name that is not declared in the catalog
- **THEN** resolution falls through to the host's own policy provider unchanged

#### Scenario: The HTTP path has no tenant

- **WHEN** a permission policy is evaluated for a principal carrying no tenant claim
- **THEN** the request is refused rather than evaluated against an unscoped permission set

### Requirement: A denial reaches an HTTP caller as 403

The framework SHALL offer a boundary component that turns an authorization denial — role,
permission or tenant-access — into an HTTP 403 response, and SHALL let every other failure through
unchanged.

#### Scenario: An authorization denial reaches the boundary

- **WHEN** an authorization or tenant-access denial propagates to the boundary
- **THEN** the response status is 403

#### Scenario: An unrelated failure reaches the boundary

- **WHEN** any other failure propagates to the boundary
- **THEN** it is not converted, and propagates unchanged

#### Scenario: Nothing fails

- **WHEN** the request completes without a failure
- **THEN** the boundary leaves the response untouched
