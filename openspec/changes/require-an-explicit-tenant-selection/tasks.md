# Tasks

## Implement

- [x] Remove the `return active[0].TenantId` fallback from
      `MembershipTenantClaimResolver.ResolveTenantAsync`, so several active memberships without a
      valid selection yield no claim.
- [x] Keep the ordering only if something still needs it. It exists to make the fallback
      deterministic; with the fallback gone, an `OrderBy` nothing reads is a line that invites the
      next person to use it again.
- [x] Leave the single-membership and valid-selection paths exactly as they are. They are the reason
      this is a narrow change rather than a rewrite.

## Tests

- [x] Several active memberships, no selection → **no claim**. This is the assertion that was
      missing; the existing coverage tested that resolution was deterministic, which a Guid sort
      satisfies.
- [x] Several active memberships, selection naming a tenant the user has since left → **no claim**,
      rather than one of the remaining memberships.
- [x] Exactly one active membership, no selection → that tenant. The regression guard for every
      ordinary user.
- [x] A valid selection → that tenant, regardless of how many memberships exist.
- [x] No active membership → no claim, unchanged.
- [x] Both entry points behave identically — `MembershipClaimsPrincipalFactory` at sign-in and
      `MembershipClaimsTransformation` per request. They share the resolver today; the test is what
      keeps that true.
- [x] **Confirm a tenant-scoped check actually fails closed without the claim**, rather than assuming
      it. The value of this change rests entirely on the missing claim being refused downstream.

## Document

- [x] `docs/guides/tenant-membership.md` — the host must obtain a selection before a multi-tenant
      user can act, and how to tell the two no-claim cases apart: `GetMembershipsAsync` returns the
      memberships, and the count is the distinction. Empty means no access; several means prompt.
- [x] Say plainly that the framework will not choose. A reader who expects a sensible default should
      find out here rather than from a support ticket.

## Rollout

- [x] CHANGELOG entry as **BREAKING**, with the migration note: a multi-tenant user without a
      selection stops receiving a claim, nothing stops compiling, and the symptom is authorization
      failures rather than an error naming the cause.
- [x] Note that the removed behaviour never matched the specification. A consumer relying on it was
      relying on a defect, which does not make the break less real for them but does explain why it
      is being removed rather than specified.
