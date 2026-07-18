# Tenant Membership and the Sign-In Tenant Claim

`Stratara.Identity.EntityFrameworkCore` owns the identity-directory plane: which tenants a user
belongs to, and which tenant-scoped roles the user holds in each. `Stratara.Identity.AspNetCore`
bridges that plane into the sign-in path, emitting the `stratara:tenant_id` claim that the
session-context middleware already reads — no middleware change, no new session shape.

The user↔tenant relationship is **many-to-many**, with roles scoped **per membership**. A
single-tenant application is simply the degenerate case of one membership per user.

## The two role levels

A `TenantMembership(UserId, TenantId, Roles, Status)` carries role names that apply **only inside
its `TenantId`**. Global platform roles — platform-administrator, developer — stay in ASP.NET
Identity's role store. The two levels are independent, and authorization components consult both.

`MembershipStatus.Active` grants access; `MembershipStatus.Pending` models an **invitation that has
been issued but not accepted** — the row exists so it can be listed and accepted transactionally,
but it confers no access anywhere in the stack.

```csharp
// Alice administers Acme and merely reads at Globex — two memberships, two role sets.
await memberships.SetMembershipAsync(new TenantMembership(alice, acme, ["TenantAdmin"]));
await memberships.SetMembershipAsync(new TenantMembership(alice, globex, ["Viewer"]));
await memberships.SetMembershipAsync(new TenantMembership(bob, acme, [], MembershipStatus.Pending));
```

## Hosting the directory tables

The tables (`tenant_membership`, `active_tenant`) live in a DbContext you own. Fold them into an
existing context — typically your ASP.NET Identity context, so everything shares **one migration
lineage** — by calling `ApplyIdentityDirectoryModel()` in `OnModelCreating`. Or derive a standalone
context from `IdentityDirectoryDbContext<TContext>`, which applies the model for you:

```csharp
public sealed class DirectoryDbContext(DbContextOptions<DirectoryDbContext> options)
    : IdentityDirectoryDbContext<DirectoryDbContext>(options);

builder.Services.AddTenantMembershipStore<DirectoryDbContext>();   // ITenantMembershipStore, scoped
```

## Working with the store

`ITenantMembershipStore` is deliberately relational-shaped — sign-in needs read-your-write
consistency. Both directions are first-class: `GetMembershipsAsync` answers "which tenants may this
user access", `GetMembersAsync` answers "who belongs to this tenant". `SetMembershipAsync` upserts
on `(UserId, TenantId)`, replacing the role set and status.

```csharp
var mine = await memberships.GetMembershipsAsync(alice);       // forward lookup, any status
var staff = await memberships.GetMembersAsync(acme);           // reverse lookup
var one = await memberships.GetMembershipAsync(alice, acme);   // null when not a member

await memberships.SetActiveTenantAsync(alice, globex);         // tenant switch
var active = await memberships.GetActiveTenantAsync(alice);    // null when never selected

await memberships.RemoveMembershipAsync(alice, globex);        // revoke one tenant's access
await memberships.RemoveAllMembershipsAsync(alice);            // user-erasure sweep (GDPR Art. 17)
await memberships.RemoveAllMembersAsync(globex);               // tenant-teardown sweep
```

`SetActiveTenantAsync` is **membership-guarded**: selecting a tenant the user holds no `Active`
membership in throws `InvalidOperationException` rather than persisting an unusable selection.
If your membership changes are event-sourced, keep emitting your events and update the store from a
projection or saga instead of calling `SetMembershipAsync` inline.

## The sign-in tenant claim

Both bridge modes resolve the same way — **persisted active-tenant selection → the user's only (or
deterministically first) active membership → no claim at all**. They differ only in *when* the
lookup runs:

| Mode | Behavior |
|---|---|
| `AddMembershipTenantClaim<TUser>()` — decorates `IUserClaimsPrincipalFactory<TUser>` | Stamps the claim at issuance, into cookies and Identity bearer tokens alike. No per-request lookup; a tenant switch applies on the next sign-in refresh. |
| `AddMembershipTenantClaimsTransformation()` — registers an `IClaimsTransformation` | Resolves live on every request, so a **tenant switch applies immediately** without re-issuing the sign-in. Costs one store lookup per request — cache behind the store when it gets hot. |

```csharp
builder.Services
    .AddTenantMembershipStore<DirectoryDbContext>()
    .AddMembershipTenantClaim<ApplicationUser>();   // after your identity + store registration
```

The two compose: a principal that already carries `stratara:tenant_id` — stamped at issuance, by an
earlier transformation run, or by your own machine-JWT minting — **passes through untouched**.

## Why fail-closed resolution matters

A user with no active membership gets **no claim** — not a default tenant, not an empty Guid. That
keeps every downstream consumer on its fail-closed path instead of silently resolving to somebody
else's data. The same rule holds for hosts whose Identity keys are not Guid-parseable: no claim is
stamped, no exception is thrown, so verify your key shape before adopting the bridge.

## Membership-backed authorization

`MembershipAuthorizationProvider` is the framework's first shipped `IAuthorizationProvider`: a
`[RequireRole]` check passes when the session's **actor** holds that role as a membership role in
the session's **data-owner tenant**. The `<TUser>` variant additionally falls back to global ASP.NET
Identity roles on a membership miss — so tenant-scoped checks cost no Identity round trip.

```csharp
// Membership roles only — the provider behind the authorizing mediator:
builder.Services.AddAuthorizingMediator<MembershipAuthorizationProvider>();

// Or membership roles ∪ global Identity roles, for components that resolve
// IAuthorizationProvider directly (the authorizing outbox dispatcher):
builder.Services.AddMembershipAuthorization<ApplicationUser>();
```

For **strict** tenant isolation, back the deny-all default with stored facts. A cross-tenant
operation then passes when the actor has an active membership in the data-owner tenant **or** holds
a configured platform role — the operator-impersonation path:

```csharp
builder.Services
    .AddStrataraTenantIsolation(o => o.Mode = TenantIsolationMode.Strict)
    .AddMembershipCrossTenantAuthorizer(o => o.CrossTenantRoles.Add("PlatformAdmin"));
```

Every one of these is fail-closed: no ambient session, no membership, a `Pending` membership, or an
unknown role all evaluate to `false`.

## See also

- The runnable `Stratara.Sample.IdentityDirectory` sample wires membership, permissions, and scoped
  settings end to end against SQLite; `Stratara.Sample.Identity` covers the sign-in side.
- [Permission-Based Authorization](require-permission.md) — the fine-grained layer these membership roles grant through.
- [Authorization Decorators](auth-decorators.md) — the `[RequireRole]`/permission enforcement that membership roles feed.
- [Enforce Tenant Isolation](enforce-tenant-isolation.md) — the tenant guard the `stratara:tenant_id` claim resolves for.
