# Stratara.Sample.IdentityDirectory

> **Derived.** The behaviour described here is specified under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them.

Shows the **identity-directory plane** — `Stratara.Identity.EntityFrameworkCore` — end to end:
who belongs to which tenant (**membership**), what they may do there (**permissions**), and what
they have configured (**scoped settings**). All three read from the same EF tables and the same
session Subject, which is why they live in one sample rather than three.

The whole story is one point: **roles are scoped per tenant, not per user.** Alice is a
`TenantAdmin` in Acme and a `Viewer` in Globex — same person, different rights, no second account.

## What to look at, in order

1. **`DirectoryDbContext.cs`** — three lines. Deriving from `IdentityDirectoryDbContext<T>` brings
   the `tenant_membership`, `active_tenant`, `setting_entry` and `api_key` tables. An app that
   already has a DbContext calls `modelBuilder.ApplyIdentityDirectoryModel()` in its own
   `OnModelCreating` instead, which keeps everything in one migration lineage.

2. **`Simulations.cs`** — two requests guarded with `[RequirePermission("sims.read")]` /
   `[RequirePermission("sims.delete")]`. The handlers carry **no** authorization code; the guard is
   the attribute, enforced by the authorizing mediator.

3. **`SampleSessionContextProvider.cs`** — a console app has no HTTP request, so the sample sets
   the Subject by hand. In an ASP.NET Core host this is `SessionContextMiddleware`
   (`Stratara.Sessions`) reading the `stratara:tenant_id` claim that the membership sign-in bridge
   emits — see `AddMembershipTenantClaim<TUser>()`.

4. **`Program.cs`** — DI wire-up plus three acts: seeding memberships, dispatching the guarded
   requests as three different (user, tenant) pairs, and resolving settings through the fallback
   chain.

## Run it

```bash
dotnet run --project samples/Stratara.Sample.IdentityDirectory
```

Expected output, abridged:

```
--- 2. Permissions: membership roles mapped through the catalog ---
  Alice @Acme — delete: allowed
  Bob @Acme — delete: DENIED (missing sims.delete)
  Alice @Globex — delete: DENIED (missing sims.delete)

--- 3. Settings: user-in-tenant -> user -> tenant -> global -> config -> default ---
  Alice @Acme — Ui.Theme=dark, Ui.Density=compact
  Bob @Acme — Ui.Theme=high-contrast, Ui.Density=comfortable
  Alice @Globex — Ui.Theme=system, Ui.Density=comfortable
```

The two `Alice` lines are the payoff: identical user id, opposite outcome, because the roles hang
off the *membership* rather than the account.

## Wire-up cheat sheet

```csharp
services
    .AddTenantMembershipStore<DirectoryDbContext>()
    .AddPermissionCatalog(c =>
    {
        c.Add("sims.read", "sims.delete");                          // declare the vocabulary...
        c.GrantToRole("TenantAdmin", "sims.read", "sims.delete");   // ...then grant it to roles
        c.GrantToRole("Viewer", "sims.read");
    })
    .AddCatalogPermissionResolver()                                 // membership roles → permissions
    .AddSettingCatalog(c => c.Add(
        new SettingDefinition("Ui.Theme", DefaultValue: "system"),
        new SettingDefinition("Ui.Density", DefaultValue: "comfortable", IsInherited: false)))
    .AddSettingStore<DirectoryDbContext>();

services.AddAuthorizingMediator<MembershipAuthorizationProvider>();  // enforces [RequirePermission]
```

This sample uses the plain registrations, which share one `DbContext` across every directory store in
a scope — right for a console program that does one thing at a time. A web host that issues directory
work concurrently within a request wants the `…FromContextFactory` variants instead, because a
`DbContext` serves one operation at a time. The trade, and why the shared form is still the default,
is in [Tenant Membership](../../docs/guides/tenant-membership.md).

`AddAuthorizingMediator<T>()` is what makes the attributes bite. Without it — or without an
`IPermissionResolver` — the mediator's startup validator throws rather than let a
permission-guarded request through unchecked.

`GrantToRole` on an undeclared permission throws at startup, so a typo surfaces on boot instead of
becoming a silent deny in production.

Use `AddCatalogPermissionResolver<TUser>()` / `AddMembershipAuthorization<TUser>()` when global
ASP.NET Identity roles (platform roles such as `PlatformAdmin`) should also count. The non-generic
pair used here consults membership roles only.

## The settings fallback chain

`ISettingProvider` resolves for the current session's **Subject**, most specific first:

```
user-in-tenant → user → tenant → global → IConfiguration (Stratara:Settings:<name>) → code default
```

`Ui.Theme` is inherited (the default), so `Bob @Acme` sees his own `high-contrast`, `Alice @Acme`
falls through to the tenant's `dark`, and `Alice @Globex` — where nothing is stored — lands on the
code default `system`.

`Ui.Density` is declared `IsInherited: false`, so **only the most specific scope is consulted**.
Alice has her own value (`compact`); Bob does *not* inherit Acme's `cozy` and lands on the default.
That flag is how you model a setting that must be answered per user or not at all.

## Erasure (GDPR Art. 17)

Both planes sweep by scope: `ITenantMembershipStore.RemoveAllMembershipsAsync(userId)` and
`ISettingStore.DeleteScopeAsync(SettingScope.ForUser(userId))` remove a user across **all** tenants.
Settings declared `IsEncrypted: true` are additionally crypto-shredded when the key scope is erased
via `IKeyStore.EraseScopeAsync` — the row can stay and stays unreadable.

## Where to go next

- **`Stratara.Sample.Identity`** — how the user gets signed in at all (external OpenID Connect +
  JIT provisioning), and how API keys authenticate machine callers into this same plane.
- **`Stratara.Sample.Validation`** — the other mediator pipeline behavior; register validation
  before authorization so invalid requests are rejected first.
