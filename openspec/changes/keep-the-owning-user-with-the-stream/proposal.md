# Keep the owning user with the stream

> **Status:** approved

## Why

Every recorded event carries the tenant **and the user** who own it, and the pair decides the key its
protected fields are encrypted under. A user's erasure destroys the keys of the scopes that name that
user. Since `anchor-event-subject-to-the-stream` (4.0.0), a later event takes its owner from the
stream's first event rather than from the session. But only the tenant is taken over. The user is
dropped, so an event appended to an existing stream in a later save records no user at all.

What that costs a consumer whose aggregate belongs to a user:

1. The first save records the stream's events under tenant A and user U, whether U came from the
   session's data-owner user or from a stated Subject. Their protected fields are encrypted under the
   scope of A and U.
2. Every later save records its events under tenant A and **no user**. Their protected fields are
   encrypted under A's tenant scope.
3. Erasing U destroys the keys of U's scopes and reaches only the first save's events. Everything
   appended later stays readable under the tenant's key.

The stream has two owners again, which is the failure `anchor-event-subject-to-the-stream` removed
for tenants. The outcome also depends on batching, which nobody chooses on purpose: within the first
save, the per-batch cache keeps the user, so the same events appended in one save or in two end up
under different owners.

## What Changes

The consumer-visible effect: a later event on an existing stream carries the whole owner recorded on
the stream's first event — its tenant, and its user where one was recorded — whatever user the session
names.

- The stream lookup in subject resolution yields the tenant and the user recorded on the stream's
  first event. It used to yield the tenant alone.
- A stream whose first event records no user keeps recording none, even when the session names a data
  owner user. That is today's behaviour for such streams, and it stays.
- A stated Subject (`AppendOnBehalfOfAsync`) still outranks the stream for its one event, user
  included. On a stream's first event it is the owner the stream records, user included.
- Entries already recorded are not rewritten. Events a later save recorded without the stream's user
  since 4.0.0 stay encrypted under the tenant scope. The changelog says so, so a consumer with
  user-owned aggregates knows that a user's erasure did not reach those events.
- The `event-sourcing-store` requirement on subject resolution is amended. It names only the tenant
  recorded on the stream; it now names the owner, tenant and user.

Not **breaking** in the sense of an API change: no signature changes. It does change recorded data,
but only for streams whose first event names a user, and only in the direction the requirement
already implies — "a stream's recorded owner is stable".

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `event-sourcing-store`: the requirement *The owning tenant is resolved from the stream before the
  session* says the stream contributes its whole recorded owner, tenant and user, and adds a scenario
  for a stream owned by a user.

## Impact

- `src/Stratara.Infrastructure/EventSourcing/EventSource.cs` — the stream lookup in subject
  resolution (`LookupExistingAggregateTenantIdAsync`), and the private summary of the resolution order.
- `src/Stratara.Abstractions/Abstractions/EventSourcing/IEventSource.cs` — the XML remarks naming
  "the tenant recorded on the stream's first event".
- `docs/guides/write-a-command-handler.md` → *Which tenant owns the events you append* — the same
  wording in the published guide.
- `tests/Stratara.Infrastructure.Tests/EventSourcing/` — tests for a user-owned stream appended to in a
  later save.
- `CHANGELOG.md` — entry under *Fixed*, including the note on events already recorded without the user.
- No package, tier, dependency or wire-format change.
