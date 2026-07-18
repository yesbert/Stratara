# Sample 8 — Identity Directory (membership, permissions, settings)

**Concept**: `Stratara.Identity.EntityFrameworkCore` — who belongs to which tenant, what they may do
there, and what they have configured. Three planes over the same EF tables and the same session
Subject.

- **Code**: [`samples/Stratara.Sample.IdentityDirectory`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.IdentityDirectory)
- **Lines**: ~200
- **Read time**: 10–15 min
- **What it doesn't have**: no HTTP, no sign-in — a console program on in-memory SQLite that sets the session Subject by hand.

## What you'll see

1. **`DirectoryDbContext`** — three lines. Deriving from `IdentityDirectoryDbContext<T>` brings the
   directory tables. (Sample 7 shows the other hosting option: fold them into an existing context
   with `ApplyIdentityDirectoryModel()`.)
2. **Membership** — Alice is seeded as `TenantAdmin` in Acme *and* `Viewer` in Globex; Bob as
   `Viewer` in Acme. Both lookup directions are exercised (a user's tenants, a tenant's members),
   plus the membership-guarded active-tenant selection.
3. **`Simulations.cs`** — a query and a command guarded with `[RequirePermission("sims.read")]` /
   `[RequirePermission("sims.delete")]`. The handlers contain **no authorization code**.
4. **`SampleSessionContextProvider`** — stands in for `SessionContextMiddleware`, which in a web host
   derives the Subject from the `stratara:tenant_id` claim.
5. **Settings** — `Ui.Theme` (inherited) and `Ui.Density` (`IsInherited: false`) resolved for three
   different (user, tenant) pairs.

## Running

```bash
dotnet run --project samples/Stratara.Sample.IdentityDirectory
```

Expected output, abridged:

```
--- 1. Membership: one user, many tenants, roles scoped per tenant ---
  Alice in Acme: [TenantAdmin]
  Alice in Globex: [Viewer]
  Acme members: 2
  Alice's active tenant: Globex

--- 2. Permissions: membership roles mapped through the catalog ---
  Alice @Acme — delete: allowed
  Bob @Acme — delete: DENIED (missing sims.delete)
  Alice @Globex — delete: DENIED (missing sims.delete)

--- 3. Settings: user-in-tenant -> user -> tenant -> global -> config -> default ---
  Alice @Acme — Ui.Theme=dark, Ui.Density=compact
  Bob @Acme — Ui.Theme=high-contrast, Ui.Density=comfortable
  Alice @Globex — Ui.Theme=system, Ui.Density=comfortable
```

## Key takeaways

- **The two `Alice` lines are the whole point.** Identical user id, opposite outcome — because roles
  hang off the *membership*, not the account. Modelling "admin here, read-only there" needs no second
  account and no role-name prefixing.
- Permissions are declared code-first and granted to roles. `GrantToRole` on an undeclared permission
  **throws at startup**, so a typo fails the boot instead of becoming a silent deny in production.
- `[RequirePermission]` only bites when an authorizing mediator and an `IPermissionResolver` are
  registered — and the startup validator throws if a guarded type exists without them. A guard cannot
  be silently skipped.
- `Ui.Theme` is inherited, so `Alice @Acme` falls through to Acme's `dark` while `Bob @Acme` keeps his
  own `high-contrast`. `Ui.Density` is declared `IsInherited: false`, so Bob does **not** inherit
  Acme's `cozy` — he lands on the code default. That flag models a setting that must be answered per
  user or not at all.

See the **[Tenant Membership](../guides/tenant-membership.md)**,
**[Permission-Based Authorization](../guides/require-permission.md)** and
**[Scoped Settings](../guides/scoped-settings.md)** guides for the full walkthrough, and
**[Sample 7](07-identity.md)** for how a caller gets authenticated in the first place.
