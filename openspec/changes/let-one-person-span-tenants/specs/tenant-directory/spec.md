## MODIFIED Requirements

### Requirement: An invited membership confers nothing

A membership SHALL carry a status distinguishing an active member from an invited one and from a
former one. Only an active membership SHALL confer roles or count as membership anywhere access is
decided; a pending or a former membership SHALL confer no roles and SHALL NOT count as membership
anywhere access is decided.

A former membership records that the user was a member of the tenant. It SHALL remain stored and
listed by the directory until the tenant's or the user's erasure removes it, so that a tenant can
still tell who once belonged to it after that person has left.

#### Scenario: A membership is pending

- **WHEN** a membership exists with pending status
- **THEN** it confers no roles, does not permit access to the tenant, and does not satisfy any check
  that requires membership

#### Scenario: A pending membership is activated

- **WHEN** a pending membership is set to active
- **THEN** its roles take effect

#### Scenario: A membership is ended

- **WHEN** an active membership is set to former
- **THEN** it confers no roles, satisfies no role, permission or cross-tenant check, is not chosen as
  the user's tenant at sign-in, and is still returned when the tenant's members or the user's
  memberships are listed

#### Scenario: A former membership is erased

- **WHEN** the user's or the tenant's erasure is performed while a former membership exists
- **THEN** the former membership is removed with every other membership the erasure covers

### Requirement: A user has at most one selected tenant, and it must be a live membership

A user SHALL have at most one persisted active-tenant selection. Selecting a tenant SHALL require an
active membership in it, and SHALL replace any previous selection rather than adding to it. A
selection SHALL be cleared when the membership it points at is removed or set to any status other
than active.

#### Scenario: A tenant is selected

- **WHEN** a user with an active membership selects that tenant
- **THEN** the selection is recorded

#### Scenario: A tenant is selected without an active membership

- **WHEN** a user attempts to select a tenant in which they have no membership, or only a pending or
  a former one
- **THEN** the selection is refused

#### Scenario: A different tenant is selected

- **WHEN** a user with an existing selection selects another tenant they are an active member of
- **THEN** the single stored selection is updated rather than a second one being added

#### Scenario: The selected membership is removed

- **WHEN** the membership a user's selection points at is removed
- **THEN** the selection is cleared, so it can never point at a membership that no longer exists

#### Scenario: The selected membership stops being active

- **WHEN** the membership a user's selection points at is set to pending or to former
- **THEN** the selection is cleared, so it never points at a membership that confers nothing

### Requirement: The tenant claim can be stamped at sign-in or resolved per request

The framework SHALL offer three modes: stamping the claim when the principal is issued; resolving it
live on each request; and keeping the claim stamped when the principal is issued while checking it on
each request. With live resolution, a change of selected tenant SHALL take effect without the user
signing in again.

In the kept-and-checked mode, the tenant a principal carries SHALL be decided when the principal is
issued — at sign-in, or when the host re-issues the sign-in, which is how the host switches the user
to the tenant it has just selected — and SHALL be kept when the framework refreshes an existing
sign-in on its own, for as long as the membership in that tenant is active. On
every request the framework SHALL verify that the user's membership in the tenant the principal
carries is active. Where it is not, the framework SHALL remove the tenant claim from that request's
principal, so that the request resolves exactly as one without a tenant claim does, and SHALL NOT put
another of the user's tenants in its place.

The kept-and-checked mode SHALL NOT be combined with live resolution; registering both SHALL fail,
naming the two, because each decides the claim differently and only one can run.

The documentation SHALL state, for each mode, when a change of membership and a change of selection
take effect. It SHALL state that a user has one selection across everything that shares the identity
store, so that live resolution moves every open session of the user when any of them switches tenant,
and that the kept-and-checked mode is the one to choose where several applications share one identity
store.

#### Scenario: The claim is resolved per request

- **WHEN** live resolution is configured and a user changes their selected tenant
- **THEN** the next request carries the new tenant without a new sign-in

#### Scenario: Live resolution runs repeatedly

- **WHEN** the same principal is transformed more than once
- **THEN** the result is the same and no duplicate claim accumulates

#### Scenario: The principal is not authenticated

- **WHEN** live resolution encounters an unauthenticated principal
- **THEN** it does nothing

#### Scenario: Another session moves the selection

- **WHEN** the kept-and-checked mode is configured, a user is signed in with tenant A, and the user's
  selection is then changed to tenant B elsewhere
- **THEN** this sign-in's requests keep carrying tenant A, including after the framework refreshes
  the sign-in on its own — verified for a cookie sign-in

#### Scenario: The host switches the tenant

- **WHEN** the kept-and-checked mode is configured, the host selects another tenant the signed-in
  user is an active member of, and re-issues the sign-in
- **THEN** the following requests carry the new tenant

#### Scenario: Both modes are registered

- **WHEN** a host registers the kept-and-checked mode and live resolution
- **THEN** registration fails, naming both

#### Scenario: The membership ends while the user is signed in

- **WHEN** the kept-and-checked mode is configured and the membership in the tenant a signed-in user
  carries is removed, set to pending or set to former
- **THEN** the user's next request carries no tenant claim and resolves as a request without one,
  without waiting for the sign-in to be refreshed, and no other of the user's tenants is substituted

#### Scenario: A refresh meets an ended membership

- **WHEN** the kept-and-checked mode is configured and the framework refreshes a sign-in whose tenant
  the user is no longer an active member of
- **THEN** the refreshed principal does not carry that tenant, and no other of the user's tenants is
  substituted — verified for a cookie sign-in

### Requirement: A subject's erasure can be performed as one operation

The framework SHALL offer a single operation that erases a subject across every plane it holds data
in — membership, key material, settings and API keys — in an order that leaves nothing unreachable
before it has been removed.

A subject's key material SHALL include every key its erasure covers — for a tenant, a key of any
level that names the tenant; for a user, a user-level key that names the user — whoever else the key
names and whether or not that other subject is still in the directory, wherever the key store can
list the keys it holds. The framework's file-backed key store and its test key store SHALL be able to.
Where a key store cannot list its keys, the erasure SHALL still shred the keys the directory names and
SHALL warn that it could not reach the rest. The keys the directory names for a user SHALL include the
user's own key — the one that names the user under no tenant or under the reserved default tenant —
so that a user's own data is shredded whether or not the key store can list its keys. An erasure SHALL
refuse an empty id, which names no subject.

Before it sweeps any plane, the erasure SHALL mark the subject erased in the key store, so that no
key covered by the erasure is created for it afterwards. An encryption racing the
erasure then either creates its key before the mark, and that key is shredded with the rest, or is
refused. Where the key store cannot mark a subject, the erasure SHALL still shred the keys it reaches
and SHALL warn that keys for the subject can be created again.

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

#### Scenario: A key names a subject that is no longer in the directory

- **WHEN** a tenant is erased and one of its keys also names a user who is not a member at that time
- **THEN** that key is shredded with the tenant's other keys

#### Scenario: An erasure is run again

- **WHEN** an erasure is performed for a subject whose memberships an earlier erasure already removed
- **THEN** it still shreds every key naming the subject that the earlier run left behind

#### Scenario: The key store cannot list its keys

- **WHEN** a subject is erased through a key store that cannot list the keys it holds
- **THEN** the keys the directory names are shredded, and a warning says that keys shared with
  someone no longer in the directory were not reached

#### Scenario: A user's own data is erased through a key store that cannot list its keys

- **WHEN** a user's own data was encrypted under the reserved default tenant and the user is erased
  through a key store that cannot list the keys it holds
- **THEN** that data becomes unrecoverable as well

#### Scenario: One of a user's tenants is erased

- **WHEN** a tenant the user belongs to is erased
- **THEN** the user's own data, written under the reserved default tenant, stays readable

#### Scenario: Work for an erased subject encrypts after the erasure

- **WHEN** a subject has been erased and work that was already running then encrypts a value under a
  key the erasure covers
- **THEN** the encryption is refused, and no key for the subject exists afterwards

#### Scenario: The key store cannot mark a subject

- **WHEN** a subject is erased through a key store that cannot mark a subject erased
- **THEN** the keys are shredded as before, and a warning says that keys for the subject can be
  created again

#### Scenario: An empty id is given

- **WHEN** an erasure is requested for an empty tenant or user id
- **THEN** it is refused before anything is swept

#### Scenario: A consumer asks what erasure covers

- **WHEN** a consumer needs to know whether an erasure is complete
- **THEN** the framework states which planes it covers and which it does not — in particular that
  read models a consumer's own projections built, and any unprotected data in the event stream, are
  the consumer's to handle

#### Scenario: A plane's sweep fails partway

- **WHEN** one plane's sweep fails during a composed erasure
- **THEN** the failure identifies which plane it was, so the operation can be resumed rather than
  restarted blindly
