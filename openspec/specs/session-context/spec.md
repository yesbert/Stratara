# session-context Specification

## Purpose
Carry one answer to "who is asking" and "whose data is this" through an entire call — from the
transport that authenticated it to the store that persists it — so that routing, encryption,
query filtering and audit stamping all agree without any of them re-deriving identity.

## Requirements

### Requirement: The session context distinguishes the actor from the data owner

The session context SHALL carry two independent identities: the **actor**, the principal who
triggered the operation, and the **data owner** (the subject), whose data the operation concerns.
Unprefixed tenant and user identifiers SHALL always mean the data owner; actor identifiers SHALL
always be named as such.

The data owner governs routing, encryption scoping and query filtering. The actor governs the
audit trail. For most operations the two are the same principal; they diverge for privileged
cross-tenant operations, anonymous requests and system or saga flows.

#### Scenario: A user operates on their own tenant's data

- **WHEN** an authenticated user issues a request against their own tenant
- **THEN** the actor identity and the data-owner identity are the same

#### Scenario: A privileged operation acts on a foreign tenant

- **WHEN** an authorized operation is promoted to act on another tenant's data
- **THEN** the data-owner tenant is the target tenant, and the actor tenant remains the tenant of
  the principal who triggered it
- **AND** anything derived from the data owner — routing, encryption scope, query filter — follows
  the target, while the audit record follows the actor

#### Scenario: A service or saga flow has no inherited actor

- **WHEN** a flow originates from the system rather than from a request
- **THEN** the actor identities are the reserved system sentinel values, which are distinguishable
  from the empty identifier used for anonymous requests

### Requirement: The connection identity is neither actor nor subject

The session context SHALL carry a client identity that names the connection an operation arrived
on — a browser tab, a phone call, a server-rendered session — separately from both the actor and
the data owner. It SHALL be optional, because not every operation has an originating connection.

#### Scenario: A request arrives with a connection identity

- **WHEN** a request carries the `X-Client-Id` header with a parsable identifier
- **THEN** that identifier is available on the session context as the client identity

#### Scenario: A request arrives without a usable connection identity

- **WHEN** the `X-Client-Id` header is absent, empty, or not parsable as an identifier
- **THEN** the client identity is absent rather than defaulted, and no request is rejected for it

### Requirement: The context is ambient and settable within a logical operation

The framework SHALL expose the current session context as an ambient value that any component in
the call can read without it being threaded through method signatures, and SHALL allow it to be
set and cleared for the duration of a logical operation.

#### Scenario: No context has been set

- **WHEN** a component reads the ambient context before anything has set it
- **THEN** it reads an absent context rather than a fabricated one

#### Scenario: A context is set and later replaced

- **WHEN** a context is set and then a second context is set
- **THEN** the second context is what subsequent readers see

#### Scenario: A context is cleared

- **WHEN** the context is cleared at the end of a unit of work
- **THEN** subsequent readers see an absent context

### Requirement: An authenticated HTTP request populates the context from its claims

For an HTTP request carrying an authenticated principal, the framework SHALL populate the session
context from that principal's claims before the request reaches application code, defaulting the
actor to the data owner.

#### Scenario: An authenticated request arrives

- **WHEN** a request arrives with an authenticated principal
- **THEN** the session context is populated for that request, with the user identity taken from
  the principal's name-identifier claim and the tenant identity taken from the
  `stratara:tenant_id` claim
- **AND** the actor and the data owner are the same principal

#### Scenario: An unauthenticated request arrives

- **WHEN** a request arrives whose principal is not authenticated
- **THEN** no session context is set for it, and the request proceeds

#### Scenario: The principal carries no name-identifier claim

- **WHEN** an authenticated principal has no name-identifier claim, or one that is not a parsable
  identifier
- **THEN** the user identity is the empty identifier rather than the request being rejected

### Requirement: Tenant resolution fails closed

Where the tenant claim is missing or unparsable, the framework SHALL resolve the data-owner tenant
to a reserved default identifier. It SHALL NOT accept a tenant supplied by the caller unless the
host has explicitly opted in.

An authenticated principal must not be able to choose which tenant its request operates against.

#### Scenario: No tenant claim and no opt-in

- **WHEN** an authenticated request has no usable `stratara:tenant_id` claim and the host has not
  opted in to the header fallback
- **THEN** the data-owner tenant is the reserved default identifier, and any `X-Tenant-Id` header
  on the request is ignored entirely

#### Scenario: The host has opted in to the header fallback

- **WHEN** the host has explicitly enabled the header fallback and an authenticated request has no
  usable tenant claim but carries a parsable `X-Tenant-Id` header
- **THEN** the data-owner tenant is taken from that header

#### Scenario: A tenant claim is present

- **WHEN** the principal carries a parsable `stratara:tenant_id` claim
- **THEN** that claim determines the data-owner tenant, and the header is not consulted even where
  the fallback is enabled

### Requirement: Setting the context stamps the current trace

Setting the session context SHALL attach the correlation identity, the causation identity, the
data-owner tenant and the actor user to the current trace span, and clearing it SHALL remove them,
so that a trace can be filtered by tenant or by the principal who triggered the work without any
component emitting those values itself.

#### Scenario: A context is set while a trace span is active

- **WHEN** the session context is set and a trace span is active
- **THEN** the span carries the correlation identity, the causation identity, the **data-owner**
  tenant and the **actor** user as tags

#### Scenario: The context is cleared while a trace span is active

- **WHEN** the session context is cleared and a trace span is active
- **THEN** those tags are removed from the span

### Requirement: Correlation survives a request that supplies none

Every populated session context SHALL carry a correlation identity, generated where the transport
supplies none, so that no unit of work is untraceable.

#### Scenario: The transport supplies a request identifier

- **WHEN** the incoming request carries a non-empty trace identifier
- **THEN** that identifier becomes the correlation identity of the session context

#### Scenario: The transport supplies no request identifier

- **WHEN** the incoming request's trace identifier is empty
- **THEN** a fresh time-ordered correlation identity is generated for the session context

### Requirement: The ambient identities are readable without reading the context

The framework SHALL expose the acting user and the data-owner tenant as narrow services, so that a
component needing only one of them — a query filter, an audit stamp — does not depend on the whole
context shape. Where no context is set, each SHALL report the empty identifier rather than failing.

#### Scenario: A query filter needs the data-owner tenant

- **WHEN** a component asks for the current tenant
- **THEN** it receives the **data-owner** tenant of the session context, not the actor's tenant

#### Scenario: An audit stamp needs the acting user

- **WHEN** a component asks for the current user
- **THEN** it receives the **actor** user of the session context, not the data owner's user

#### Scenario: No context is set

- **WHEN** either service is asked while no session context is set
- **THEN** it reports the empty identifier and the caller is not required to handle an absent value
