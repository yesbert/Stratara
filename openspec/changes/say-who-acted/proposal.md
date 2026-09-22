# Say who acted

> **Status:** approved

## Why

The framework models the actor and the data owner as two independent identities, and then, at two
places, decides as though they were one. A service that holds one machine key and names the tenant
per request — the shape of every integration that serves many tenants — has its role looked up in
the tenant it is acting *on*, where it holds no membership and never can: a key materialises
exactly one membership, in the tenant it was issued for. Every guarded request it makes is refused.

The same confusion refuses work the platform starts for a tenant. A durable timer, a saga step or a
nightly sweep has no inherited actor, so the framework offers reserved system sentinel values for
exactly that case — and no code anywhere acts on them. Under strict isolation a session that names
the platform as actor is an ordinary cross-tenant request that the shipped deny-everything
authorizer refuses. The honest session is refused and the session that passes says the tenant did
something the platform did. The concept documentation's own system-flow example is of the first
kind.

Two shipped promises are affected. `tenant-directory` states that a configured cross-tenant role
permits an operation "without any membership in the subject tenant", but the role behind it is
looked up in the subject tenant, so on the membership level that scenario cannot pass. And
`session-context` states that a system flow's actor identities are the reserved sentinels, without
anything downstream treating them as such.

## What Changes

- Role checking against membership gains a configured set of role names that resolve in the tenant
  the actor is a member of, rather than in the tenant the request concerns. Empty by default, so no
  host's behaviour changes on upgrade, and even once configured only the named roles cross — a
  tenant administrator's role stays in their own tenant unless someone names it.
- The shipped cross-tenant authorizer's configured platform roles are evaluated where the actor
  holds them, which is what the existing requirement already promises. Today they are evaluated in
  the subject tenant, so the promise holds only for a host that also keeps global platform roles.
- Work the platform starts on a tenant's behalf gains a session shape of its own: a documented way
  to build it, and recognition by the tenant-isolation guard, which records it as
  platform-initiated instead of referring it to the cross-tenant authorizer. A host that would
  rather decide for itself can keep referring it.
- The documentation says in which tenant a role is looked up, and the system-flow example becomes
  one that runs.

Nothing is removed and no signature changes. A host that configures nothing sees no difference,
except that a platform-initiated session is no longer refused.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `tenant-directory`: membership role checking gains the configured set of role names resolved in
  the actor's own tenant, as a level between membership in the subject tenant and global roles.
- `tenant-isolation`: platform-initiated work is recognised as such rather than treated as a
  cross-tenant operation, with a host-level switch to refer it to the authorizer anyway.
- `session-context`: the reserved system actor identities gain a stated meaning and a documented
  way to build a session that carries them.

## Impact

- `src/Stratara.Identity.EntityFrameworkCore/MembershipRoleEvaluator.cs`,
  `MembershipAuthorizationProvider.cs`, `MembershipAuthorizationProviderOfTUser.cs` — the added
  level, and the options that carry it.
- `src/Stratara.Identity.EntityFrameworkCore/MembershipCrossTenantAuthorizer.cs` — the configured
  platform roles evaluated where the actor holds them.
- `src/Stratara.Contracts/Session/SessionContext.cs` — a way to build the platform session beside
  the sentinels it already declares.
- `src/Stratara.Mediator/Multitenancy/TenantIsolationGuard.cs`, `TenantIsolationOptions.cs`, and the
  log events beside the cross-tenant pair.
- `docs/guides/api-keys-and-pats.md`, `docs/concepts/session-context.md`,
  `docs/guides/enforce-tenant-isolation.md` — where a role is looked up, and an example that runs.
- Depends on `read-what-was-written`: a platform session must carry a causation identity, which that
  change makes a refusal rather than a constraint violation.
- Nothing is dissolved or superseded by this change.
