# Design — A context per operation for the directory stores

## Context

See `proposal.md` — *Why*.

The shape today. Three stores back the directory plane, one per store contract, each generic over the
consumer's context type and each taking that context as a constructor parameter. All three are
registered `TryAddScoped` from `IdentityDirectoryServiceCollectionExtensions`, so within a request
they resolve the one scoped context the consumer registered and share it. Every method reaches
`context` directly; between them they call `SaveChangesAsync` eight times.

The settings registration is not a plain `TryAddScoped<TInterface, TImplementation>` — it builds the
store inside a factory delegate and conditionally wraps it in an encrypting decorator, so whatever is
done for the other two has to fit that composition as well.

Evidence: the source as it stands at `c7e1b181`, and a consumer's framework-findings report of
2026-08-26.

## Goals / Non-Goals

**Goals:**

- A consumer can register the directory stores so that concurrent directory work inside one request
  does not contend.
- Consumers who change nothing keep exactly today's behaviour, including its transaction behaviour.
- Both costs are stated where the choice is made, not in a document a consumer reaches afterwards.

**Non-Goals:**

- Recommending one registration over the other.
- Changing the store contracts, or which of them exist.
- Removing the stores' own commits. That is the root of the flush hazard and changing it would move
  transaction semantics for every consumer; it deserves its own change and its own argument.
- Pooling, context lifetime tuning, or anything else about how the consumer's factory is configured.

## Decisions

### One code path through a context seam, not a second set of stores

Each store acquires a context for the duration of one operation from an internal seam, and the
registration decides what that seam does: hand back the request's shared context and dispose nothing,
or create a fresh context and dispose it when the operation ends. Every store method becomes "acquire,
use, release" regardless of which registration is in play.

The alternative was a decorator per store that opens a context and delegates to a freshly constructed
inner store. It leaves the existing stores untouched, which is tempting — but it duplicates all three
contracts as pure forwarding code, roughly twenty methods whose only job is to be kept in step with
the originals. That is the kind of duplication that is correct on the day it is written and wrong two
contracts later. The seam costs an edit to every existing method once; the decorator costs a
maintenance obligation forever.

The seam also keeps the default path honest: it is the same code, exercised by the same tests, with a
seam implementation that borrows instead of owning. A second implementation of the stores would mean
the path most consumers use and the path the new tests cover are different code.

**Ownership is the seam's whole job.** Borrowing must not dispose the consumer's context; owning must.
Getting that backwards in the borrowing direction disposes a context the consumer still needs, so the
seam carries ownership explicitly rather than inferring it.

### The new registrations are separate methods, not a flag

`AddTenantMembershipStoreFromContextFactory<TContext>()` and its two siblings, alongside the existing
`AddTenantMembershipStore<TContext>()`. A boolean parameter on the existing method would read as
`AddTenantMembershipStore<AppDbContext>(true)` at the call site, which says nothing. The name states
what the consumer must have registered — a context factory — which is the prerequisite most likely to
be missed.

Missing that registration fails at first resolution rather than silently, matching how the settings
registration already handles a missing encryptor.

### Both registrations keep `TryAdd`, and the file says so

Consistent with every other registration in this file, and with the framework's habit of letting a
consumer call a registration twice without harm. The consequence is that a consumer who calls both
the shared and the factory registration keeps whichever ran first, silently.

That is a real trap, and it is the same *kind* of trap this change exists to remove, so it is named in
the documentation of both methods rather than left to be discovered. The alternative — having the
factory registration replace an existing one — trades a silent wrong answer for an order-dependent
one, which is harder to reason about and no more discoverable. Calling both is a configuration
mistake; the honest fix is to say so, not to guess which the consumer meant.

### Documentation carries the trade, not just the constraint

The brief that reported this asked for the one-operation constraint to be documented. That alone
would leave a consumer able to see one side of the choice: they would learn that sharing is
constrained, adopt the factory, and discover afterwards that their store write no longer joins the
transaction they opened. Both registrations therefore state both consequences — what sharing costs,
and what a context per operation costs — so the choice can be made once rather than twice.

## Risks / Trade-offs

- **Every existing store method is edited.** → The largest risk in the change, and it is churn rather
  than design risk: the existing tests cover the shared path and must stay green without being
  rewritten. If a test needs changing to accommodate the seam, that is a signal the seam changed
  behaviour and should stop the change rather than be absorbed.
- **A consumer adopts the factory and loses a transaction they were relying on.** → Only for writes,
  only on adoption, and stated at the registration. Worth noting that what they lose is smaller than
  it looks: the stores already commit on their own, so a consumer relying on a store write to be
  rolled back with their own work was relying on something the shared context gave them by accident.
- **A consumer registers both.** → Silent, first-wins, documented. Named above as an accepted
  trade rather than a missed one.
- **A pooled factory returns a context with the consumer's own interceptors or filters configured
  differently from their scoped registration.** → Out of the framework's hands; the consumer
  configures both. Mentioned in the documentation as something to keep aligned.

## Migration Plan

No migration. Nothing existing changes, and adoption is one registration call plus an
`AddDbContextFactory` if the consumer does not already have one. Rollback is switching the
registration back, or the previous package version.
