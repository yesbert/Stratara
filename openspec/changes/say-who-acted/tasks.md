## 1. A role the host names resolves in the actor's own tenant

- [x] 1.1 Add an options type in `Stratara.Identity.EntityFrameworkCore` carrying the set of role
  names, shaped like `MembershipCrossTenantAuthorizerOptions` (an `ISet<string>` with an ordinal
  comparer, empty by default).
- [x] 1.2 Extend `MembershipRoleEvaluator.IsInMembershipRoleAsync` with the second level: on a miss
  in the subject tenant, and only when `session.ActorTenantId != session.TenantId` and the role is
  named, read the actor's membership in `session.ActorTenantId`.
- [x] 1.3 Thread the options through `MembershipAuthorizationProvider` and
  `MembershipAuthorizationProvider<TUser>`, keeping the global-role level last in the generic one.
- [x] 1.4 Register the options from the membership authorization registrations in
  `IdentityDirectoryServiceCollectionExtensions`, with a configure overload; the existing
  parameterless registrations keep working unchanged.
- [x] 1.5 Tests in `tests/Stratara.Identity.EntityFrameworkCore.Tests`: a named role held only in
  the actor's tenant passes; an unnamed role held there fails; with an empty set every existing case
  resolves as before; a same-tenant session does exactly one membership lookup.

## 2. The cross-tenant authorizer finds the role where the actor holds it

- [x] 2.1 In `MembershipCrossTenantAuthorizer`, resolve the configured `CrossTenantRoles` against the
  actor's own membership as well as the global role store, instead of through a role check that
  looks only in the subject tenant.
- [x] 2.2 Tests: an actor whose only role level is its membership at home passes by a configured
  role; an actor with neither is still refused; the existing membership-in-subject-tenant path is
  unchanged.
- [x] 2.3 Verify `MembershipCrossTenantAuthorizer`'s XML documentation still describes what it does
  — it already claims the operator-impersonation path this task makes real.

## 3. A session for work the platform starts

- [x] 3.1 Add the factory to `SessionContext` beside `Empty()`: takes the tenant, returns the system
  actor identities, a fresh correlation identity and a minted causation identity. Document it —
  CS1591 is an error in `Stratara.Contracts`.
- [x] 3.2 Tests in `tests/Stratara.Shared.Tests` (where the contracts' session types are covered):
  the factory's session carries both sentinels, a causation identity, and the given tenant as data
  owner.

## 4. The guard recognises platform-initiated work

- [x] 4.1 In `TenantIsolationGuard.EnsureAuthorizedAsync`, add the branch before the cross-tenant
  one: both actor identities equal to the reserved system values is platform-initiated work — the
  authorizer is not consulted. Keep the subject check ahead of it.
- [x] 4.2 Add the log event beside `LogCrossTenantAllowed` / `LogCrossTenantRejected` in
  `LoggerTenantIsolationExtensions`, with its own event id in `LogEvents.TenantIsolation`.
- [x] 4.3 Add the setting to `TenantIsolationOptions` that refers platform-initiated work to the
  authorizer anyway, defaulting to recognising it.
- [x] 4.4 Tests in `tests/Stratara.Infrastructure.Tests/Multitenancy/TenantIsolationBehaviorTests.cs`: platform session passes strict mode without the authorizer being called; a half-built
  session — one sentinel only — is still treated as cross-tenant; the setting refers it; a request
  targeting another tenant is still refused by the subject check.

## 5. Documentation

- [x] 5.1 `docs/concepts/session-context.md`: replace the system-flow example (around line 108) with
  the factory, and state that such a session passes strict tenant isolation.
- [x] 5.2 `docs/guides/api-keys-and-pats.md`: state in which tenant a machine key's roles are looked
  up once an endpoint has promoted the subject, and what naming a role in the new option means.
- [x] 5.3 `docs/guides/enforce-tenant-isolation.md`: platform-initiated work and the setting that
  refers it to the authorizer.
- [x] 5.4 `docs/guides/queue-background-work.md` and
  `docs/guides/operate-the-orleans-execution-model.md`: point a timer or saga handler at the factory
  where they show a handler that dispatches.
- [x] 5.5 `CHANGELOG.md` under Unreleased. The cross-tenant-authorizer fix is a loosening of a
  security decision — say so in those words.

## 6. Gate

- [x] 6.1 `openspec validate say-who-acted --strict`
- [x] 6.2 `./scripts/local-gauntlet.sh`
- [x] 6.3 `dotnet test tests/Stratara.Orleans.IntegrationTests` (Docker) — the guard sits in the
  execution model's dispatch path.
