> **Status:** approved

# Reject an ownerless event subject, and let a tenant declare itself its own owner

## Why

Every recorded event carries a **subject** — the tenant that owns the data, whose key encrypts the
payload and whose erasure reaches it. The framework resolves it through five candidates and refuses
to guess when all of them are empty. Two paths get past that refusal and record an event with no
owner at all, or with the wrong one.

**An explicitly supplied subject is never checked.** Of the five candidates, four are validated
against "no tenant": the stream's recorded owner, the creation event's tenant and the session's
tenant are each rejected when empty, and an append with nothing left fails. The first candidate —
the subject the caller states deliberately — is taken as given. Appending on behalf of an empty
subject therefore records an entry owned by nobody and encrypts its payload against nobody. The one
path a caller takes *when they mean to name the owner* is the only one that does not check that an
owner was named. Consumers reach it by passing a field off their own aggregate that a stream written
before the field existed never filled.

**The framework's own tenant-creation event does not declare its tenant.** It carries the new
tenant's id, but not as the creation-event tenant, so creating a tenant falls all the way through to
the session. A tenant created by an operator is owned by the *operator's* tenant; a caller who
happens to point the session at the new id first gets a tenant that owns itself. The same operation
produces two different owners depending on whether the caller knew to take a detour — and the detour
is undiscoverable, so most callers will not.

Both are one-line defects with consequences that outlive the append: a shredded key never reaches an
entry owned by nobody, and a tenant filed under the wrong owner stays there.

## What Changes

- **BREAKING** — Appending on behalf of a subject that names no tenant fails, naming the stream and
  the event, instead of recording an ownerless entry. A caller who was explicit is told their
  subject was empty rather than having it silently replaced by one of the other candidates, which
  would contradict what they asked for.
- The framework's tenant-creation event declares the tenant it creates as that event's owner, so a
  newly created tenant owns its own stream regardless of which session performed the creation. No
  payload change and no stored-data migration: the declaration is a contract the framework reads at
  append time, not a serialized field.
- **BREAKING** in recorded data for tenant creation: a consumer whose operator session created
  tenants until now saw them owned by the operator's tenant. New creations are owned by the new
  tenant. Streams already written are untouched.

Out of scope, deliberately: whether the tenant aggregate should also count as tenant-scoped over
itself, so that the stream's recorded owner wins for its *later* events too. That question belongs
to `anchor-event-subject-to-the-stream`, which is reworking exactly that condition.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `event-sourcing-store`: the requirement covering subject resolution states that an explicitly
  supplied subject overrides every other source. It gains the condition that the subject must name a
  tenant, and a scenario for an explicit subject that names none.
- `tenant-directory`: the requirement covering the event-sourced tenant aggregate gains the
  guarantee that a tenant's creation event is owned by the tenant it creates, not by the session
  that performed the creation.

## Impact

- `Stratara.Infrastructure` — subject resolution on the event source.
- `Stratara.Domain` — the tenant-creation event; `Stratara.Domain` already sits on
  `Stratara.Abstractions`, so declaring the creation contract adds no package reference and no tier
  crossing.
- Consumers appending on behalf of a subject sourced from their own aggregate state: a field that
  was empty used to be accepted and now throws. That is the fix, and it surfaces at the append
  rather than at the erasure that fails to reach the entry years later.
- Source of both findings: a consumer's framework-findings report. No legacy source is dissolved or
  superseded by this change.
