## MODIFIED Requirements

### Requirement: The owning tenant is resolved from the stream before the session

The tenant an event belongs to SHALL be resolved in this order: an explicit subject supplied by the
caller for that event; the subject already established for that stream in the current batch; the
tenant recorded on the stream's first existing event; a tenant carried by the event itself where the
event declares itself a creation event; and only then the session's data-owner tenant. Where none of
these yields a tenant, the append SHALL fail rather than guess.

Reading the tenant from the stream before the session is what stops a privileged operator's session
silently re-homing an existing aggregate into another tenant. That reasoning does not depend on the
aggregate's shape, so neither does the rule: **a stream's recorded owner is stable for every
aggregate**, whether or not the aggregate exposes its tenant as a property.

An aggregate whose events carry different owners cannot be fully erased — each tenant's erasure
reaches only its own entries — and, once one of those keys is shredded, cannot be rehydrated at all,
because the remaining entries are decrypted under a key that no longer exists. A consumer that
genuinely wants an event attributed to another subject SHALL state it explicitly rather than obtain
it by omission.

#### Scenario: An existing tenant-scoped stream is appended to

- **WHEN** an event is appended to an existing stream
- **THEN** the tenant recorded on that stream is used, even if the session names a different one,
  and regardless of whether the aggregate exposes a tenant of its own

#### Scenario: A new tenant-scoped aggregate is created

- **WHEN** the first event of an aggregate declares itself a creation event carrying a tenant
- **THEN** that tenant is used

#### Scenario: The caller supplies the subject explicitly

- **WHEN** the caller appends on behalf of a stated subject
- **THEN** that subject is used for that event, overriding every other source, and the override
  applies to that event only

#### Scenario: Nothing identifies a tenant

- **WHEN** no explicit subject, no stream history, no creation event and no session tenant is
  available
- **THEN** the append fails with a message naming the event, the stream, and the three ways to
  supply a subject
