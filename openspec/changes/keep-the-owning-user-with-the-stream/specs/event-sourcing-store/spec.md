## MODIFIED Requirements

### Requirement: The owning tenant is resolved from the stream before the session

The tenant an event belongs to SHALL be resolved in this order: an explicit subject supplied by the
caller for that event; the subject already established for that stream in the current batch; the
owner recorded on the stream's first existing event; a tenant carried by the event itself where the
event declares itself a creation event; and only then the session's data-owner tenant. Every
candidate SHALL name a tenant to be used, including the explicitly supplied one. Where none of these
yields a tenant, the append SHALL fail rather than guess.

The owner a stream contributes SHALL be the whole owner recorded on its first event: its tenant, and
its user where one was recorded. A stream whose first event names a user SHALL give that user to
every later event for which no subject is stated, whatever user the session names; a stream whose
first event names no user SHALL give such events none. This SHALL hold alike for events appended in the save that created the
stream and for events appended in any later save.

An explicit subject that names no tenant SHALL fail the append rather than fall through to the
remaining candidates, because a caller who stated the subject has already said which other candidate
is not to be used.

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

- **WHEN** the caller appends on behalf of a subject whose tenant is absent
- **THEN** the append fails with a message naming the event and the stream, no event is recorded,
  and the remaining candidates are not consulted

#### Scenario: Nothing identifies a tenant

- **WHEN** no explicit subject, no stream history, no creation event and no session tenant is
  available
- **THEN** the append fails with a message naming the event, the stream, and the three ways to
  supply a subject
