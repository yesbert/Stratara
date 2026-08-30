> **Status:** approved

# Require an explicit tenant selection instead of guessing one

## Why

A user may belong to several tenants — that is a deliberate capability and stays. What must not
happen is the framework deciding *which* of them a request acts in.

Today it decides. When a user has several active memberships and no valid selection, the tenant
claim falls back to the first entry of the memberships sorted by `TenantId`:

```csharp
var active = memberships.Where(Active).OrderBy(m => m.TenantId).ToList();
if (active.Count == 0) return null;                  // no claim — fails closed
…
return active[0].TenantId;                           // several — guesses
```

`OrderBy` on a `Guid` uses `Guid.CompareTo`, which compares byte groups in an order of its own. The
winner is therefore not the oldest membership, not the alphabetically first tenant, and not the
user's primary one. It is an artefact of Guid comparison semantics. **Whatever a user expects, this
is not it.**

The result is a write in the wrong tenant, with nothing reported. Tenant isolation is one of the
framework's headline guarantees, and this is the one path that can breach it silently: a *missing*
claim fails visibly, a *wrong* claim fails invisibly.

### This is not an open question — the specification already answered it

The `tenant-directory` requirement states the order plainly:

> *"resolving in this order: the user's persisted active-tenant selection, then their only active
> membership where they have exactly one, and **otherwise no claim at all**."*
>
> *"Emitting no claim is the fail-closed outcome: it resolves to the reserved default tenant rather
> than to an arbitrary one of several."*

The code emits a claim. **It contradicts the requirement it implements**, and has since the
requirement was written.

What let the two drift apart is one hedged scenario — *"the resolution is deterministic rather than
arbitrary, and where it cannot be determined no claim is emitted"*. "Deterministic rather than
arbitrary" is satisfiable by sorting on anything at all, so a Guid sort passes it while defeating the
requirement's own sentence. That scenario is the reason nothing caught this, and tightening it is as
much a part of this change as the code.

So finding **TD-1** framed this as two cases decided differently with the reasoning unrecorded. The
reasoning *was* recorded — in the requirement. Only the implementation never followed it.

## What Changes

The consumer-visible effect: a user with several active memberships and no valid selection receives
**no tenant claim**, exactly as a user with no memberships does. Every tenant-scoped check then fails
closed, and the host must route that user to a selection before they can act.

- A user with exactly one active membership is unaffected. That case is unambiguous and keeps
  resolving to that membership — otherwise every ordinary user would break.
- A user with a valid stored selection is unaffected. The selection is what the change makes
  mandatory, not what it removes.
- The `tenant-directory` requirement covering claim resolution keeps its sentence and loses its
  loophole: the hedged scenario is replaced by one that states the outcome, so a future
  implementation cannot satisfy the scenario while defeating the requirement.
- The derived documentation gains what a consumer now has to build: how to tell "no access at all"
  apart from "several tenants, none chosen", since both present as a missing claim. Both are already
  answerable through `GetMembershipsAsync`, so this needs documentation rather than new API.

**Breaking**, and invisible at compile time: nothing stops compiling, a multi-tenant user simply
stops receiving a claim they used to receive. It belongs in a major version — notwithstanding that
the behaviour being removed was never specified in the first place. What a consumer relies on is what
ships, not what the requirement said.

## Capabilities

### Modified Capabilities

- `tenant-directory`: the requirement describing how the sign-in tenant claim is resolved drops the
  deterministic-first fallback, and states that an ambiguous membership set yields no claim.

## Impact

- `src/Stratara.Identity.AspNetCore/Services/MembershipTenantClaimResolver.cs` — `ResolveTenantAsync`,
  the single place the fallback lives. Both callers, `MembershipClaimsPrincipalFactory` (at sign-in)
  and `MembershipClaimsTransformation` (per request), go through it, so there is one thing to change.
- `docs/guides/tenant-membership.md` — the derived page, which must now cover the selection
  obligation and how to distinguish the two no-claim cases.
- `openspec/specs/tenant-directory/spec.md` — the claim-resolution requirement.
- The API-key path is **not** affected. `ApiKeyAuthenticationHandler` stamps the key's own tenant
  directly; a key belongs to exactly one tenant and nothing is inferred.
- Resolves **TD-1**, carried as an open decision in `resolve-consistency-findings`.
- No package, tier or dependency change.
