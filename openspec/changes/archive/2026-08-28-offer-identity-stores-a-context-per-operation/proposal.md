> **Status:** approved

# The directory stores can be given a context per operation, and say what they cost either way

## Why

The stores that read and write the identity directory — memberships, API keys, settings — each take
the consumer's database context directly and are registered per request. Every one of them in a
request therefore shares a single context instance.

**A database context permits one operation at a time.** A consumer who fans directory work out
inside a request — two role checks issued together, a lookup racing a page load — gets a failure from
whichever operation arrives second, reporting that a second operation was started on the same
context. Nothing in the registration hints that this is a constraint. The failure surfaces at the
call site that lost the race, which is usually not the one that introduced the concurrency, so it
reads as an unrelated defect. The consumer who reported this had repaired the same disturbance three
times in three places before anyone looked at the shape that permits it.

**Sharing the context costs something else, which nobody has reported yet.** The stores commit their
own writes. On a shared context, committing also commits whatever the consumer is holding unsaved on
it — so writing a membership in the middle of the consumer's own work flushes their half-finished
entities along with it. Same shape, different failure, and it does not need any concurrency at all.

A consumer can work around both today only by opening their own scope per operation, which is what
the reporting consumer did. That covers their call sites and nobody else's.

## What Changes

- **Each directory-backed store can be registered to take a fresh context for each operation**,
  through a new registration alongside the existing one. Operations then no longer contend, so
  fanning directory work out inside a request is safe.
- **The existing registration is untouched** and stays the default. Switching the default would
  change transaction behaviour under consumers who have not asked for it.
- **Both registrations state what they cost**, because a consumer can only choose if both sides are
  named:
  - Sharing the request's context means one operation at a time, and a store's commit also commits
    the consumer's pending work.
  - A context per operation means neither of those — and, in exchange, a store's write no longer
    takes part in a transaction the consumer opened on their own context.

Not breaking: the new registrations are additional, nothing existing changes shape or behaviour, and
a consumer who changes nothing sees nothing.

Out of scope, deliberately:

- Which registration is recommended. The framework offers both and describes both; naming a
  preference is a separate decision, and the honest one to make later, once consumers have used the
  new path.
- Any change to the store contracts themselves. This is about how a store reaches its context, not
  about what a store does.
- The stores committing their own writes at all. That is the deeper question behind the second
  hazard above, it would change transaction semantics for everyone, and it is not this change.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tenant-directory`: it already guarantees that the directory's tables can be hosted in a context
  the consumer already has. It gains the neighbouring guarantee about how the directory's stores
  reach that context — that a consumer can choose between sharing the request's context and taking
  one per operation, and that the framework states the consequence of each.

`api-keys` and `scoped-settings` need no delta. Their stores are affected in exactly the same way,
but neither capability says anything today about how a store obtains its context — that question
belongs to the directory's hosting requirement, which is where the answer is going.

## Impact

- `Stratara.Identity.EntityFrameworkCore` — the three stores and their registrations.
- Consumers: no action required. A consumer who has been serialising directory work by hand, or
  who has hit the second-operation failure, can adopt the new registration and stop.
- Source: a consumer's framework-findings report. No legacy source is dissolved or superseded by
  this change.
