# Design

## Context

See `proposal.md` for the motivation. Issuing a user-bound key already requires an active membership;
validating one does not. A machine key's identity is its own membership, which issuing writes and
revoking removes; a user-bound key writes no membership and rides the user's. Keys and memberships are
always in the same store, because issuing already queries both on one context (evidence: the issuance
guard in `src/Stratara.Identity.EntityFrameworkCore/EfApiKeyStore.cs`).

## Goals / Non-Goals

**Goals:**

- A user-bound key authenticates only while its user is an active member of the key's tenant.
- The answer lives in `IApiKeyStore.ValidateAsync`, so every caller of it gets it, not only the HTTP
  scheme.

**Non-Goals:**

- Revoking or otherwise writing to a token when a membership ends.
- Any change to machine keys, including what happens if a machine key's membership row is altered.
- Any change to the kept-and-checked tenant-claim mode of `tenant-directory`.
- Telling the caller why validation failed. The contract stays fail-closed and undifferentiated.

## Decisions

### Check at validation, not at the moment the membership changes

Rejected: revoking the host's tokens when a membership ends. The owner decided on 2026-11-06 that the
token works again when the membership returns, and revocation is permanent ("a revoked key is never
reinstated"). A check at use also needs no hook on the membership write paths, which include direct
store writes and erasure sweeps. Evidence: the owner's decision; `RevokeAsync` and the import refusal in
`EfApiKeyStore.cs`.

### Put it in the store contract, not in the authentication handler

Rejected: checking in `ApiKeyAuthenticationHandler`. A consumer calling `ValidateAsync` directly would
still get a descriptor for the former member, and the handler would need a second dependency and a
second round trip. The cost of the chosen option is that a custom `IApiKeyStore` must make the check as
well; the interface's XML documentation and the guide say so. Evidence: the handler takes only
`IApiKeyStore` and treats `null` as failure.

### One query in the EF store

The key is read with a correlated existence check over the membership, projected in the same statement,
so validation stays one round trip on the shared-context registration and the per-operation one alike,
and key and membership are read from one snapshot. The membership's primary key serves the probe, and
the check is evaluated only for a key with a bound user. Rejected: a second query after the key is
found, which is simpler to read but adds a round trip to every token request. The lookup of a key by
digest is otherwise unchanged. Evidence: the composite primary key in
`src/Stratara.Identity.EntityFrameworkCore/Configurations/TenantMembershipEntryConfiguration.cs`; the
statement is verified by the tests in task group 1.

### The shared identity store takes the same path

`AddApiKeyStore` and `AddApiKeyStoreFromContextFactory` register the same store over different context
sources, and the shared identity store uses one of them. There is no second implementation to change,
so it receives one test against a real database to prove the mapping translates there, not a parallel
matrix. Evidence: `src/Stratara.Identity.EntityFrameworkCore/DependencyInjection/IdentityDirectoryServiceCollectionExtensions.cs`.

### How this fits `tenant-directory`

`tenant-directory` says, of the kept-and-checked mode, that a principal another authentication scheme
issued, "an API key, a token the host mints", keeps the tenant it carries, and has a scenario in which
such a principal's id has no membership and the claim stays. Both stay true and are not modified:

- That requirement describes what the claim check does to a principal that **exists**. This change acts
  earlier: a user-bound key without an active membership yields **no principal at all**, so authentication
  fails and there is no claim to keep or to remove. It does not make the claim check inspect API keys.
- A key that does validate keeps the tenant it carries, as that requirement says. The tenant of a
  user-bound key is fixed at issuance, and validation has just confirmed the membership in it.
- A machine key's principal, and a token the host mints, still pass through untouched. The scenario's
  "no membership" case remains reachable by those: a host-minted token, or a machine key whose row was
  removed by something other than revocation.

The outcome also differs deliberately from the signed-in user's. A cookie user who loses a membership
stays authenticated without a tenant claim, because they remain a person who may hold other tenants and
may select one. A token is bound to exactly one tenant and has no other purpose, so it fails outright:
401 rather than a request in the reserved default tenant. Rejected: removing only the claim, which would
leave a principal authenticated by a token that cannot be used for anything, and would open endpoints
that require only authentication in the default tenant. Evidence: the "A principal of another scheme
carries a tenant" and "The membership ends while the user is signed in" scenarios of
`openspec/specs/tenant-directory/spec.md`. `docs/guides/tenant-membership.md` gets one clarifying clause,
since its "an API key keeps the tenant it carries" would otherwise read as contradicting this change.

### The in-memory store reads the membership through its membership plane

`InMemoryApiKeyStore` already guards issuance through its `Memberships` store. Validation does the same,
after leaving its lock, because the membership store's API is asynchronous and must not be awaited under
it. Evidence: `src/Stratara.Testing/InMemoryApiKeyStore.cs`.

## Risks / Trade-offs

- [A custom `IApiKeyStore` silently keeps the old behaviour; nothing fails to compile.] → The XML
  documentation of `ValidateAsync` and the guide state the obligation; the release's changelog entry has
  to say so too.
- [A consumer relied on a token surviving a pending or former membership, for example a service account
  modelled as a user.] → It now gets a 401. That is the decided behaviour; a machine key is the supported
  shape for an identity with no membership of a person. The guide says so.
- [A test using `InMemoryApiKeyStore` that issues a token and then removes the membership now sees a
  failed validation.] → Intended, since the double mirrors the contract; the task list includes running
  the suites that use it.
- [Every token validation evaluates a little more SQL.] → A primary-key probe in the same statement, on
  user-bound keys only.

## Migration Plan

None required: no schema change. The rule applies to existing tokens from the first validation after
the upgrade. Rolling back restores the former behaviour, since nothing was written.
