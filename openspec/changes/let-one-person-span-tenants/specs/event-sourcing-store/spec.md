## MODIFIED Requirements

### Requirement: The owning tenant is resolved from the stream before the session

The tenant an event belongs to SHALL be resolved in this order: an explicit subject supplied by the
caller for that event; the subject already established for that stream in the current batch; the
owner recorded on the stream's first existing event; a tenant carried by the event itself where the
event declares itself a creation event; and only then the session's data-owner tenant. Every
candidate SHALL name a tenant to be used, except that an explicitly supplied subject, and the owner a
stream recorded from one, MAY name a user alone. Where none of these yields an owner, the append SHALL
fail rather than guess.

An owner that names a user alone records data that belongs to a person rather than to any of their
tenants. The event SHALL be recorded under the reserved default tenant with that user, and the
stream SHALL keep that owner for every later event for which no subject is stated, whatever tenant
the appending session names. Only a caller that states the owner SHALL be able to create such a
stream; a session whose tenant resolved to the reserved default SHALL still give no owner, so a
request that failed closed never writes data owned by nobody in particular.

The owner a stream contributes SHALL be the whole owner recorded on its first event: its tenant, and
its user where one was recorded. A stream whose first event names a user SHALL give that user to
every later event for which no subject is stated, whatever user the session names; a stream whose
first event names no user SHALL give such events none. This SHALL hold alike for events appended in
the save that created the stream and for events appended in any later save.

An explicit subject that names neither a tenant nor a user SHALL fail the append rather than fall
through to the remaining candidates, because a caller who stated the subject has already said which
other candidate is not to be used.

Reading the tenant from the stream before the session is what stops a privileged operator's session
silently re-homing an existing aggregate into another tenant. That reasoning does not depend on the
aggregate's shape, so neither does the rule: **a stream's recorded owner is stable for every
aggregate**, whether or not the aggregate exposes its tenant as a property.

An aggregate whose events carry different owners cannot be fully erased — each tenant's or user's
erasure reaches only its own entries — and, once one of those keys is shredded, cannot be rehydrated
at all, because the remaining entries are decrypted under a key that no longer exists. A consumer that
genuinely wants an event attributed to another subject SHALL state it explicitly rather than obtain
it by omission.

#### Scenario: An existing tenant-scoped stream is appended to

- **WHEN** an event is appended to an existing stream
- **THEN** the tenant recorded on that stream is used, even if the session names a different one,
  and regardless of whether the aggregate exposes a tenant of its own

#### Scenario: An existing user-owned stream is appended to

- **WHEN** an event is appended, in a later save, to a stream whose first event was recorded for a
  tenant and a user
- **THEN** the event is recorded for that tenant and that user, even if the session names a
  different user or none, so an erasure of that user reaches every event of the stream

#### Scenario: A stream recorded without a user is appended to by a session naming one

- **WHEN** an event is appended to a stream whose first event names no user, under a session whose
  data-owner user is set
- **THEN** the event is recorded with no user, as the stream's first event was

#### Scenario: A new tenant-scoped aggregate is created

- **WHEN** the first event of an aggregate declares itself a creation event carrying a tenant
- **THEN** that tenant is used

#### Scenario: The caller supplies the subject explicitly

- **WHEN** the caller appends on behalf of a stated subject
- **THEN** that subject is used for that event, overriding every other source, and the override
  applies to that event only

#### Scenario: The caller supplies a subject that names no tenant

- **WHEN** the caller appends on behalf of a subject whose tenant and user are both absent
- **THEN** the append fails with a message naming the event and the stream, no event is recorded,
  and the remaining candidates are not consulted

#### Scenario: A stream owned by a user alone is appended to under a tenant's session

- **WHEN** a stream was created for an owner that names a user alone, and an event is appended to it
  in a later save under a session naming a tenant and another user
- **THEN** the event is recorded for that user alone, under the reserved default tenant, as the
  stream's first event was

#### Scenario: Nothing identifies a tenant

- **WHEN** no explicit subject, no stream history, no creation event and no session tenant is
  available
- **THEN** the append fails with a message naming the event, the stream, and the three ways to
  supply a subject

### Requirement: A new stream's owner can be stated at creation, and a host can require it to be

A caller SHALL be able to create a stream with an owner it states, and that owner SHALL be the one the
stream records, so every later event on the stream for which no subject is stated keeps it. Creating with
a stated owner SHALL fail as creating does when the stream already exists, and SHALL fail as an explicit
subject does when the stated owner names neither a tenant nor a user. A stated owner MAY name a user
alone, for data that belongs to a person rather than to any of their tenants.

A host SHALL be able to decide what happens when the first event of a stream that does not exist yet
would take its owner from the session — that is, when no subject is stated for it and it carries no tenant
of its own as a creation event. The host SHALL be able to allow it, as before; to allow it and record a
warning naming the stream, the event and the tenant taken from the session; or to refuse it, in which case
the append SHALL fail before anything is staged, with a message naming the stream, the event and the ways
to state an owner. Allowing it SHALL be the default. The decision SHALL NOT affect an event whose owner is
stated, a creation event that carries a tenant, or any append to a stream that already exists.

The session decides only the first event's owner now that a stream keeps the owner it was created with —
but it decides it silently, and the session belongs to whoever is acting: an operator creating a record in
someone else's tenant keys the record's whole history to their own.

#### Scenario: A stream is created with a stated owner

- **WHEN** a stream is created with a stated owner, and an event is appended to it later under a session
  naming another tenant
- **THEN** both events are recorded for the stated owner

#### Scenario: A stream that exists is created with a stated owner

- **WHEN** creation with a stated owner is attempted for a stream that already exists
- **THEN** it fails, with a message naming the stream and directing the caller to append instead

#### Scenario: The stated owner names no tenant

- **WHEN** a stream is created with a stated owner whose tenant and user are both absent
- **THEN** the creation fails with a message naming the event and the stream, and nothing is staged

#### Scenario: The stated owner names a user alone

- **WHEN** a stream is created with a stated owner that names a user and no tenant
- **THEN** the stream is created, its events are recorded for that user under the reserved default
  tenant, and the user's encrypted fields are keyed to the user alone

#### Scenario: The host allows the session to decide

- **WHEN** the host has made no decision, and a new stream's first event names no tenant and no owner is
  stated
- **THEN** the event is recorded for the session's tenant, as before, and nothing is recorded about it

#### Scenario: The host asks to be warned

- **WHEN** the host chose to be warned, and a new stream's first event takes its owner from the session
- **THEN** the event is recorded for the session's tenant, and a warning names the stream, the event type
  and that tenant

#### Scenario: The host refuses

- **WHEN** the host chose to refuse, and a new stream's first event would take its owner from the session
- **THEN** the append fails before anything is staged, with a message naming the stream, the event type and
  the ways to state an owner

#### Scenario: The host refuses, and the owner is stated another way

- **WHEN** the host chose to refuse, and a new stream is created with a stated owner, or with a creation
  event that carries a tenant, or an event is appended to a stream that already exists
- **THEN** the event is recorded as it would be without the decision
