# Permission-Based Authorization

> **Derived page.** The behaviour described here is specified by the `authorization` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

`Stratara.Identity.EntityFrameworkCore` layers fine-grained permissions over the coarse roles of
[`[RequireRole]`](auth-decorators.md). A role answers "who is this person"; a permission answers
"what may they do" — `sims.read`, `billing.write`. The contracts live in
`Stratara.Abstractions.Authorization`, so a request type can declare its guard without taking a
dependency on the resolver that evaluates it.

The vocabulary is declared in code at startup, and permissions are resolved per request from the
membership store — never carried in a claim or a token. Both halves are enforced at the same
mediator boundary every dispatch already crosses.

## Declare the vocabulary

The `PermissionCatalog` is the single source of what permission names exist, plus which roles grant
them. Declare it once during service registration:

```csharp
builder.Services.AddPermissionCatalog(c =>
{
    c.Add("sims.read", "sims.delete");
    c.GrantToRole("TenantAdmin", "sims.read", "sims.delete");
    c.GrantToRole("Viewer", "sims.read");
});
```

That catalog is the whole grant map — roles on the left, the permissions they confer on the right:

| Role | Grants |
|---|---|
| `TenantAdmin` | `sims.read`, `sims.delete` |
| `Viewer` | `sims.read` |

`Add` declares; `GrantToRole` maps; `Contains`, `GetRolePermissions`, and `All` read it back. Build
it completely at registration and treat it as immutable afterwards.

## Guard a request

`[RequirePermission]` marks the command or query, exactly as `[RequireRole]` does — the handler
itself carries no authorization code:

```csharp
using Stratara.Abstractions.Authorization;

[RequirePermission("sims.read")]
public sealed record ListSimulationsQuery : IQuery<IReadOnlyList<string>>;

[RequirePermission("sims.delete")]
public sealed record DeleteSimulationCommand(string Name) : ICommand<bool>;
```

Multiple attributes are **ANDed** — every listed permission must be held — and they compose freely
with `[RequireRole]` on the same type, since roles and permissions are independent gates:

```csharp
[RequireRole("TenantAdmin")]
[RequirePermission("billing.write")]   // role AND permission
public sealed record IssueCreditCommand(Guid InvoiceId, decimal Amount) : ICommand;
```

`AuthorizingMediator` and `AuthorizingCommandOutboxDispatcher` enforce the attribute before the
handler is resolved. A denial throws `PermissionAuthorizationException`, whose `RequiredPermission`
names the missing permission. It derives from `AuthorizationException`, so an existing role-era 403
mapping catches permission denials unchanged.

## Resolve the permissions

`IPermissionResolver.ResolvePermissionsAsync(userId, tenantId, ct)` is the lookup behind the
attribute. The default maps the actor's tenant-scoped membership roles through the catalog's grants:

```csharp
builder.Services
    .AddTenantMembershipStore<DirectoryDbContext>()
    .AddCatalogPermissionResolver()                       // membership roles only
    .AddAuthorizingMediator<MembershipAuthorizationProvider>();
```

`AddCatalogPermissionResolver<TUser>()` additionally folds in global ASP.NET Identity roles for
platform-level grants. Both memoize per `(userId, tenantId)` within a scope — repeated checks in one
request cost one membership lookup — and both fail closed: no membership, or a non-active one,
yields an empty set.

Resolution uses the session's `ActorUserId` (who triggered) against the data-owner `TenantId` (whose
data), which is why the same person holds different rights in different tenants: Alice is a
`TenantAdmin` in Acme and a `Viewer` in Globex, so `sims.delete` is allowed in Acme and denied in
Globex — one account, one catalog. Roles are scoped per membership, not per user.

## Gate HTTP endpoints

`AddStrataraPermissionPolicies()` (`Stratara.Identity.AspNetCore`) turns every declared catalog
permission into an on-demand ASP.NET Core policy, so endpoints gate on the same vocabulary:

<!-- stratara-snippet-ignore: names a permission constant the consumer declares -->
```csharp
builder.Services.AddStrataraPermissionPolicies();

app.MapGet("/sims", ListSims).RequireAuthorization("sims.read");   // or [Authorize("sims.read")]
```

The user id comes from the name-identifier claim and the tenant scope from `stratara:tenant_id` —
the claim the membership sign-in bridge stamps. Undeclared policy names defer to the default
provider, so your existing policies keep working.

## Why the defaults matter

Three defaults make a misconfigured permission impossible to ship quietly:

- **Granting an undeclared permission throws.** `GrantToRole("TenantAdmin", "sims.raed")` raises
  `ArgumentException` at startup rather than silently never matching. A typo in a grant is a boot
  failure, not a permission that mysteriously does nothing.
- **The startup validator fails fast.** A host carrying `[RequirePermission]` types without an
  authorizing mediator or without a registered `IPermissionResolver` refuses to start. The attribute
  can never be silently ignored — the failure mode of a guard that quietly does not run is an open
  door.
- **Permissions are never in claims or tokens.** They are resolved per request from the store,
  memoized only for the scope. Revoking a role or a membership takes effect on the *next request* —
  there is no stale cookie or long-lived bearer token still carrying yesterday's grants until it
  expires.

## See also

- The runnable `Stratara.Sample.IdentityDirectory` sample declares this exact catalog and shows
  Alice allowed to delete in Acme but denied in Globex.
- [Authorization Decorators](auth-decorators.md) — the `[RequireRole]` sibling and the mediator
  boundary both guards share.
- [Tenant Membership and the Sign-In Tenant Claim](tenant-membership.md) — the membership roles and
  the `stratara:tenant_id` claim these permissions resolve from.
