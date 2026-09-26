## Context

Subject resolution in the event source runs in this order: a stated Subject, the per-batch cache, the
stream's first entry, a creation event's tenant, the session. The per-batch cache holds whatever the
first event of the batch resolved, user included. The stream lookup reads the first entry but returns
only its tenant, so the user survives only within the save that resolved it. See proposal.md → *Why*
for the consequence.

Evidence:
- The implementation: the stream lookup returns `firstEntry?.TenantId`, and resolution wraps it as a
  Subject with no user.
- The tests that fixed the tenant rule in `anchor-event-subject-to-the-stream`: they assert only the
  tenant, which is why the dropped user went unnoticed.

## Goals / Non-Goals

**Goals:**
- A later save gives an event the same owner, tenant and user, as the save that created the stream.

**Non-Goals:**
- Rewriting entries already recorded without the user. The store is append-only, and an entry's
  protected fields are encrypted under the scope it was recorded with; re-encrypting them would be a
  migration tool, not a fix.
- Changing what a creation event contributes. It carries a tenant and no user, so a stream it
  creates keeps naming no user. That is today's behaviour and it stays.
- Taking the user from the session when the stream names none. That would reintroduce per-event
  ownership for the user, which is the failure the tenant rule exists to prevent.

## Decisions

**The stream lookup returns the first entry's whole owner.** The lookup yields the tenant and user of
the stream's first entry, or nothing when the stream has no entry with a tenant. Resolution uses that
owner unchanged.
- *Alternative:* take the tenant from the stream and the user from the session, as happened for a
  plain aggregate before 4.0.0. Rejected, because a stream would then have one owner per user who
  appends to it, and a user's erasure could never cover the whole aggregate.
- *Alternative:* return the user only when the session names the same one. Rejected, because the
  session does not decide ownership of an existing stream for the tenant, and has no better claim for
  the user.

**No new cache or lookup.** The first entry is already read for its tenant. Taking its user from the
same row adds no query.

## Risks / Trade-offs

- [Risk] A consumer that recorded a stream's first event under a user by accident, because its session
  sets the data-owner user for every request, now sees every later event on that stream under that
  user as well. A user's erasure then destroys the protected fields of the whole aggregate instead of
  a part of it. → This is the stable-owner rule the requirement already states. The session-context
  capability leaves the data-owner user unset by default for exactly this reason. The changelog names
  the change.
- [Risk] Events recorded since 4.0.0 without the stream's user stay under the tenant scope, so a user's
  erasure does not reach them. → The changelog says so plainly. A consumer that needs those fields
  gone can erase the tenant scope or re-record the aggregate.

## Migration Plan

None required. The change affects only events appended after upgrading. Rolling back restores the
tenant-only lookup; events recorded in the meantime keep the user they were written with.
