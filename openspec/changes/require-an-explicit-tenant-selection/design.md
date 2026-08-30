## Context

See `proposal.md` — *Why*. The whole fallback is four lines in one internal method:

```csharp
var active = memberships.Where(Active).OrderBy(m => m.TenantId).ToList();
if (active.Count == 0) return null;
var selection = await GetActiveTenantAsync(userId);
if (selection is { } selected && active.Any(m => m.TenantId == selected)) return selected;
return active[0].TenantId;                                  // <- this line
```

`MembershipTenantClaimResolver.ResolveTenantAsync` is the only place it lives. Both callers —
`MembershipClaimsPrincipalFactory` at sign-in and `MembershipClaimsTransformation` per request — go
through it, so there is one thing to change and no risk of the two paths diverging.

The API-key path is separate and unaffected: `ApiKeyAuthenticationHandler` stamps the key's own
tenant, and a key belongs to exactly one.

## Goals / Non-Goals

**Goals:**

- The implementation matches the requirement it already had.
- The scenario that allowed the drift no longer allows it.
- A consumer can tell the two no-claim cases apart, because they call for different responses.

**Non-Goals:**

- Changing multi-tenant membership. A user belonging to several tenants is the capability this
  protects, not one it restricts.
- Changing the single-membership or valid-selection paths.
- Adding a selection UI or a default-tenant policy. Which tenant a user should be prompted with, and
  in what order, is the host's product decision.

## Decisions

### Decision 1 — Return no claim rather than picking

**Chosen:** delete the `return active[0].TenantId` fallback.

**Alternative considered — pick, but pick something meaningful** (most recently used, earliest
joined, a `IsPrimary` flag on the membership). Rejected on two grounds. It invents a concept the
directory does not have, so it means new stored state and a migration. And more importantly it does
not fix the failure: a *plausible* wrong tenant is worse than an obviously arbitrary one, because
nobody looks twice at it.

### Decision 2 — Close the loophole in the scenario, not just the code

The requirement said "otherwise no claim at all" and its scenario said "deterministic rather than
arbitrary". A Guid sort satisfies the scenario while defeating the sentence, which is exactly what
happened. The scenario is replaced with one that states the outcome, so a future implementation
cannot pass the test while breaking the rule.

A second scenario is added for the stale-selection case — a selection naming a tenant the user has
since left, with several other memberships remaining. That is the path most likely to be
reintroduced by someone reasoning "they had a selection, so pick something close to it".

### Decision 3 — Documentation, not new API, for telling the two cases apart

A host now has to distinguish "no access at all" from "several tenants, none chosen": the first is a
dead end, the second is a prompt. Both present as a missing claim.

Both are already answerable — `GetMembershipsAsync` returns the memberships, and its count is the
distinction. No new API is warranted; what is missing is the derived page saying so. Adding a result
type or a second resolver method would be surface for something a consumer can already ask.

## Risks / Trade-offs

- **A multi-tenant user's first request after sign-in now fails until they choose.** That is the
  intended outcome and it is also real work for the host: a route reachable without a tenant claim.
  Hosts that already offer tenant switching have the screen; hosts that quietly relied on the guess
  do not, and will discover it at upgrade.
- **The break is invisible at compile time.** Nothing stops compiling; a claim simply stops being
  issued. Only a migration note carries it, which is why this belongs in a major version even though
  the behaviour it removes was never specified.
- **A host that misreads "no claim" as "no access" will show the wrong message** to a multi-tenant
  user — a dead end instead of a prompt. That is the direct cost of not adding a distinguishing API,
  and the reason the documentation task is not optional bookkeeping.
- **This narrows what the framework decides on the consumer's behalf.** That is deliberate. Choosing
  a tenant is a product decision, and the framework has no basis for it beyond sort order.
