## ADDED Requirements

### Requirement: A command can name the owner of its protected fields

A command SHALL be able to name the tenant, and optionally the user, that its fields marked for
encryption belong to, by implementing `INamesDataOwner`. Wherever the framework serializes such a
command — to record it before its handler runs, and to carry it to a handler that runs later — it
SHALL encrypt the marked fields for the named owner and not for the session the command was dispatched
under, and SHALL write every copy for the same tenant and the same user.

The session says who is acting and what they may do. It does not say whose data a command carries: a
command dispatched from a session with no tenant, or from the session of another tenant than the one
its data belongs to, still carries somebody's data, and only the command knows whose.

The record of a command that names an owner SHALL name that owner as its data owner and the session's
actor as its actor, so that a tenant's view of the recorded commands holds the commands that carried
its data, whoever issued them.

A command that implements the declaration and names an empty tenant, or an empty user beside its
tenant, SHALL be refused where it is recorded and where it is dispatched to run later, with an error
that names the command's type, and nothing SHALL be recorded or sent for it. It SHALL NOT fall back to
the session's tenant: a command that says it has an owner and names none is a mistake in the caller,
and encrypting it for whoever is acting would hide the mistake in valid ciphertext.

The owner SHALL be taken as the command states it. The framework does not check that the named tenant
exists, and encrypting for an owner creates key material for it where there was none. The
documentation SHALL say so, and SHALL say that the owner is to be named from something the dispatching
code trusts and not from what a caller supplied.

Naming an owner SHALL NOT change the session a handler runs under, and SHALL NOT grant, check or
replace any authorization. Whether the caller may act on that owner's data is decided as it is for a
command that names none — by the stages the host registered and by the handler — and the
documentation SHALL say so.

A recorded command SHALL remain after its owner is erased, as the evidence that it was issued, by whom
and when; its protected fields SHALL read as absent from then on, because the keys they were written
for are gone. Where the whole command type is marked for encryption rather than individual fields,
the whole recorded payload reads as absent. The documentation of erasure SHALL state both halves.

A command that names no owner SHALL be serialized as it was before a command could name one: its
protected fields are written for the session's data-owner tenant — together with the session's
data-owner user in the record, and with the acting user in the copy carried to a later handler.

#### Scenario: A command naming an owner is dispatched from a session without a tenant

- **WHEN** a host outside development dispatches a command that carries a tenant-scoped field and
  names a tenant as its owner, from a session that names no tenant
- **THEN** the command is accepted, its field is ciphertext written for the named tenant wherever the
  framework stores it, and its handler receives the field's value

#### Scenario: A command naming an owner is dispatched from another tenant's session

- **WHEN** a command that names one tenant as its owner is dispatched from a session whose data owner
  is a different tenant
- **THEN** its protected fields are written for the tenant it names, not for the session's, and the
  handler runs under the dispatching session unchanged

#### Scenario: A command names a user as well

- **WHEN** a command names a tenant and a user as its owner and auditing is registered
- **THEN** the recorded copy and the copy carried to its handler are both written for that tenant and
  that user

#### Scenario: A command naming an owner is recorded

- **WHEN** a command that names an owner is dispatched with auditing registered
- **THEN** its record names that owner as the data owner and the dispatching session's actor as the
  actor

#### Scenario: A command names an empty tenant as its owner

- **WHEN** a command that implements the declaration and names an empty tenant is dispatched
- **THEN** it is refused with an error naming its type, and nothing is recorded or sent

#### Scenario: A command names an empty user beside its tenant

- **WHEN** a command that names a tenant and an empty user as its owner is dispatched
- **THEN** it is refused with an error naming its type, and nothing is recorded or sent

#### Scenario: A command names a tenant nobody has registered

- **WHEN** a command names as its owner a tenant the host has never seen
- **THEN** it is accepted and its protected fields are written under key material created for that
  tenant, because the owner is taken as stated — which is why the documentation tells the dispatching
  code to name an owner it trusts

#### Scenario: The named owner is erased after the command was recorded

- **WHEN** a tenant's erasure is performed after a command naming that tenant as its owner was
  recorded
- **THEN** the record is still there with its type, time, actor and owner, and its protected fields
  read as absent

#### Scenario: Naming an owner does not authorize

- **WHEN** a command that names an owner is dispatched by a caller the host's authorization or tenant
  isolation would refuse
- **THEN** it is refused exactly as it would be without the declaration

#### Scenario: A command names no owner

- **WHEN** a command that does not implement the declaration is dispatched
- **THEN** its protected fields are written for the session's data-owner tenant — in the record
  together with the session's data-owner user, in the copy carried to a later handler together with
  the acting user — as before a command could name an owner
