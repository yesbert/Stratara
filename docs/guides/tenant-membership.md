# Tenant Membership and the Sign-In Tenant Claim

> **Derived page.** The behaviour described here is specified by the `tenant-directory` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

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

### One context for the request, or one per operation

That registration — and its siblings `AddApiKeyStore<TContext>()` and `AddSettingStore<TContext>()` —
gives every directory store in a request **the same** context instance. Two things follow, and both
are easier to meet head-on than to meet as a bug:

- A database context serves one operation at a time. Directory work issued concurrently inside one
  request — two role checks started together, a lookup racing a page load — fails on whichever
  arrives second, and it fails at the call site that lost the race rather than the one that
  introduced the concurrency.
- The stores commit their own writes. On a shared context, that commit also commits whatever *you*
  have left unsaved on it.

If either matters to you, register the stores against a context factory instead:

```csharp
// configure the factory with the same provider options as your AddDbContext call
builder.Services.AddDbContextFactory<DirectoryDbContext>(options => { /* … */ });

builder.Services
    .AddTenantMembershipStoreFromContextFactory<DirectoryDbContext>()
    .AddApiKeyStoreFromContextFactory<DirectoryDbContext>()
    .AddSettingStoreFromContextFactory<DirectoryDbContext>();
```

Each operation then takes a fresh context and disposes it, so operations do not contend and a store's
commit reaches only its own rows. **In exchange, a store write no longer takes part in a transaction
you opened on your own scoped context** — that is the whole of the trade, and it is why the shared
registration remains the default rather than being quietly replaced.

Keep the factory's options aligned with your scoped registration: interceptors, query filters and
conventions are configured per registration, not per context type. Calling both registrations for the
same store leaves whichever ran first in place; they do not compose.

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

## Erasing a subject

Removing memberships is one plane of an erasure, not the whole of it. `ISubjectEraser` composes the
four sweeps the framework owns and runs them in an order that leaves nothing unreachable before it
has been removed:

```csharp
services.AddStrataraErasure();   // the four stores it sweeps are registered separately

var report = await eraser.EraseUserAsync(alice);
// report.Planes: ApiKeys -> Settings -> Memberships -> KeyMaterial
```

**Why that order.** API keys go first, so nothing can act on the subject's behalf while the erasure
runs. Key material goes last, because shredding it makes every other plane unreadable — sweep it
first and a later failure leaves rows nobody can identify. The memberships are *read* before they
are removed, because they are what tells the eraser which tenants the subject has settings and keys
in.

**If a plane fails, the erasure stops there** and raises `ErasureIncompleteException`, naming the
plane and listing what was already swept. It does not continue, precisely so that a failed settings
sweep never leads to the key being shredded anyway. Resume from the named plane.

**What it does not cover, and this matters as much as what it does:**

- **Read models your own projections built.** The framework does not know they exist. They are
  yours to sweep.
- **Event-stream data not protected by a scoped key.** Removing a key shreds what that key
  encrypted; anything written in the clear is still there.
- **The command audit log and the outbox.** Both carry a session context naming the subject, and
  both are deliberately left alone — the audit log is the evidence that the erasure happened, and
  whether to retain it is a decision only you can take for your jurisdiction.
- **System-wide (`Confidential`) key material**, which is not subject-scoped and is never erased.

## See also

- The runnable `Stratara.Sample.IdentityDirectory` sample wires membership, permissions, and scoped
  settings end to end against SQLite; `Stratara.Sample.Identity` covers the sign-in side.
- [Permission-Based Authorization](require-permission.md) — the fine-grained layer these membership roles grant through.
- [Authorization Decorators](auth-decorators.md) — the `[RequireRole]`/permission enforcement that membership roles feed.
- [Enforce Tenant Isolation](enforce-tenant-isolation.md) — the tenant guard the `stratara:tenant_id` claim resolves for.
