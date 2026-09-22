## Context

See `proposal.md` — Why. Two reports from a consumer running 4.2.0, both about the same seam:
Stratara carries the actor and the data owner separately, and two decisions read only one of them.

What shapes the approach:

- `MembershipRoleEvaluator` is the single place both membership authorization providers resolve a
  role, and it asks `GetMembershipAsync(session.ActorUserId, session.TenantId)` — the actor's
  identity in the *subject's* tenant. `MembershipAuthorizationProvider<TUser>` already layers a
  second level (global Identity roles) behind it, so a third level fits an established shape.
- `MembershipCrossTenantAuthorizer` checks its configured roles through `IAuthorizationProvider`,
  which is that same evaluator. For a host without the global-role variant the configured role can
  therefore never be found, because it is looked for in the one tenant the requirement says the
  actor need not be a member of. A machine actor has no global role level at all: `api-keys` states
  that a key becomes a membership and that there is no second evaluator.
- `SessionContext.SystemActorTenantId` and `SystemActorUserId` have existed since the contract was
  written and are documented in `docs/concepts/session-context.md`. Nothing in `src/` reads them.
- `TenantIsolationGuard` has exactly one branch for actor ≠ subject, and two log events beside it.
- `TimerOwnerGrain.FireAsync` runs a handler in a fresh scope with no session. Its `ownerId` is an
  arbitrary grain key, so the framework cannot know which tenant the work is for; the handler can.

## Goals / Non-Goals

**Goals:**

- An actor whose membership is in another tenant than the one it acts on can hold a role the host
  has named, without a consumer writing its own authorization provider.
- The cross-tenant authorizer's configured roles work for the actors the requirement names.
- Work the platform starts has one session shape, it passes strict isolation, and the audit trail
  says the platform did it.

**Non-Goals:**

- Changing where *permissions* are resolved. `authorization` states they are resolved for the actor
  within the data owner's tenant, a permission set is a different object from a role name, and no
  report has asked for it. If it turns out to be the same gap, it is a change of its own.
- Setting the session inside the timer grain. It cannot know the tenant (see Context); the handler
  builds the session, in one call.
- A membership that marks a machine actor as such. The first draft of this change made the new
  level automatic for machine actors, which needs the evaluator to tell a key from a user:
  `TenantMembership` carries no such mark and `IApiKeyStore` has no lookup by identity, so it would
  cost a new method on a published interface or a second query on every role check. Naming roles
  costs neither and is narrower.
- Making the session's actor unforgeable. Anything that can set a session can set any actor today;
  recognising the system sentinel adds no way in that was not already open.

## Decisions

### A named set of roles, not a switch

`MembershipAuthorizationProvider` (both variants) gains options carrying a set of role names that
resolve in the actor's own tenant. The evaluator's order becomes: the actor's active membership in
the subject tenant, then — only for a named role, and only when the actor's tenant differs from the
subject's — the actor's active membership in its own tenant, then, for the global variant, the
global role store.

*Alternative rejected — evaluate every role in the actor's tenant.* The simplest rule, and wrong: a
user who is a member of two tenants and switches to the second must be checked in the second. Their
roles differ per membership, which `tenant-directory` states as a first-class property.

*Alternative rejected — a boolean "also look in the actor's tenant".* One switch carries every role
the actor holds at home into every tenant it acts on. An operator with a tenant-administrator role
at home would silently become a tenant administrator everywhere they are allowed to act. Naming the
roles keeps the blast radius at what the host wrote down, and it mirrors the existing
cross-tenant-roles option, which is the same idea one decision further out.

Evidence: reported against 4.2.0 by a consumer whose service holds one machine key and names the
tenant per request; `MembershipRoleEvaluator.cs:24-25` and `EfApiKeyStore.MachineMembershipFor`,
which writes the key's single membership in the key's own tenant.

### Fix the cross-tenant authorizer rather than document its limit

`MembershipCrossTenantAuthorizer` resolves its configured roles where the actor holds them: the
actor's own membership, and the global role store where one is in use. It stops depending on a role
check that looks in the tenant the actor is, by the requirement's own words, not a member of.

This admits actors that are refused today, which is a loosening of a security decision and belongs
in the release notes in those words. It is a loosening towards what the requirement has always
said, and the host still has to have named the role.

*Alternative rejected — write the limitation into the requirement.* It would mean stating that the
configured platform role works only for hosts that keep global platform roles, which turns the
machine-actor case — the case the option is most useful for — into something the framework cannot
do. The requirement is right; the implementation is not.

### Recognise the system actor in the guard, and offer the strict host a way out

`TenantIsolationGuard` gains a branch before the cross-tenant one: an actor whose tenant *and* user
are both the reserved system values is platform-initiated work. It is permitted, logged under its
own event, and the authorizer is not consulted. `TenantIsolationOptions` gains a setting for a host
that wants such a session referred to its authorizer like any other.

Both sentinel values are required, not just the tenant, so that a half-built session — a system
tenant with a real user, or the reverse — is not quietly treated as platform work.

*Alternative rejected — leave it to each consumer's `ICrossTenantAuthorizer`.* That is the status
quo, and it makes every consumer of the framework's own timers write the same clause. It also means
the framework ships a background-work feature that its own strict mode refuses by default, which is
how the report arrived.

*Alternative rejected — a distinct sentinel per flow kind (timer, saga, sweep).* More precise in the
audit trail, and a new vocabulary to define, migrate to and explain. The correlation identity
already distinguishes one flow from another.

Evidence: `SessionContext.cs:40,46` and `docs/concepts/session-context.md:41` declare the sentinels;
`grep` over `src/` finds no reader of either. `TenantIsolationGuard.cs:43-48` is the branch that
refuses them under the shipped deny-everything authorizer.

### The platform session is built by a factory on the contract

`SessionContext` gains a factory beside `Empty()` that takes the tenant the work is for and returns
a session with the system actor identities, a fresh correlation identity and a causation identity.
It mints the causation identity rather than leaving it absent, so that platform work can append
without a command having preceded it — which, after `read-what-was-written`, is otherwise refused.

*Alternative rejected — leave it to documentation.* The shape is five fields, three of which are
easy to get subtly wrong, and getting the actor wrong is exactly the failure this change exists to
fix.

## Risks / Trade-offs

- **The new role level is a privilege escalation if a host names the wrong role.** → It is empty by
  default, only named roles cross, and the guide states what naming one means. A host that names its
  tenant-administrator role has decided that it means something across tenants.
- **The cross-tenant authorizer starts permitting operations it refused.** → Only where the host had
  already configured the role, which is an instruction the host wrote and that the requirement has
  always said would work. Called out in the release notes.
- **Recognising the sentinel widens what passes strict isolation.** → Only for a session that names
  both system identities, which no authenticated request produces; the subject check still applies,
  so such a session reaches exactly one tenant; and a host that objects can switch it off.
- **Two role lookups instead of one for a cross-tenant request with a named role.** → Only on a
  membership miss in the subject tenant, only when the actor's tenant differs, and only when a role
  is named. The ordinary same-tenant request is untouched and does the single lookup it does today.
- **The audit trail changes shape for consumers who already fake a platform session by naming the
  tenant as actor.** → Nothing forces them to adopt the factory; their sessions keep working.

## Migration Plan

No migration. No schema change, no required configuration. Ordered after
`read-what-was-written` because the platform session's causation identity is only meaningful once
an append without one is refused rather than failing in the database.

Adoption for a consumer whose machine actor is refused today: name the key's role in the new option,
and drop the authorization provider they wrote to work around it.
