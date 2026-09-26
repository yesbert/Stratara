## MODIFIED Requirements

### Requirement: A subject's erasure can be performed as one operation

The framework SHALL offer a single operation that erases a subject across every plane it holds data
in — membership, key material, settings and API keys — in an order that leaves nothing unreachable
before it has been removed.

A subject's key material SHALL include every key its erasure covers — for a tenant, a key of any
level that names the tenant; for a user, a user-level key that names the user — whoever else the key
names and whether or not that other subject is still in the directory, wherever the key store can
list the keys it holds. The framework's file-backed key store and its test key store SHALL be able to.
Where a key store cannot list its keys, the erasure SHALL still shred the keys the directory names and
SHALL warn that it could not reach the rest. An erasure SHALL refuse an empty id, which names no
subject.

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
