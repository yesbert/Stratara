# Scoped Settings

> **Derived page.** The behaviour described here is specified by the `scoped-settings` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

`Stratara.Abstractions.Settings` declares the settings plane — a vocabulary of named values that
resolve differently per tenant, per user, or per user-in-tenant. `Stratara.Identity.EntityFrameworkCore`
ships the EF Core storage and the read facade. It answers "what did *this* person, in *this* tenant,
configure?" without every feature inventing its own preferences table.

The vocabulary is declared in code and enforced strictly: an undeclared name is a throw, not a silent
`null`. Values are stored per scope and read through one fallback chain fed by the session's
**Subject** (data-owner) ids — never the actor.

## Declare the vocabulary

A `SettingDefinition` is a dotted name, a code default, and two flags. Declare the whole catalog at
startup and treat it as immutable — `Add` throws an `ArgumentException` on a duplicate name, so two
features cannot quietly fight over one setting:

```csharp
builder.Services
    .AddSettingCatalog(c => c.Add(
        new SettingDefinition("Ui.Theme", DefaultValue: "system"),
        new SettingDefinition("Ui.Density", DefaultValue: "comfortable", IsInherited: false),
        new SettingDefinition("Smtp.Password", IsEncrypted: true)))
    .AddSettingStore<DirectoryDbContext>();
```

`AddSettingStore<TContext>()` registers both halves: the `ISettingStore` SPI over the `setting_entry`
table and the `ISettingProvider` read facade on top of it.

## Write exactly, read by fallback

The two interfaces are deliberately asymmetric. `ISettingStore` addresses **one exact scope** — it is
catalog-unaware and untyped, and it is what an admin UI or a preferences endpoint writes through.
Passing `null` as the value **deletes** the entry:

```csharp
await store.SetAsync("Ui.Theme", "dark", SettingScope.ForTenant(acme));
await store.SetAsync("Ui.Theme", "high-contrast", SettingScope.ForUserInTenant(acme, bob));
await store.SetAsync("Ui.Theme", null, SettingScope.ForUserInTenant(acme, bob));  // deletes
```

`ISettingProvider` reads for the *current session* — no scope argument, because the Subject supplies
it. `GetAsync<T>` converts via `TypeDescriptor` under `InvariantCulture`:

```csharp
var theme = await settings.GetOrNullAsync("Ui.Theme");
var pageSize = await settings.GetAsync("Ui.PageSize", defaultValue: 25);
```

Each resolved name is memoized for the provider's lifetime — register it scoped, and that is one
request. Only that per-request memoization ships: there is no distributed cache invalidation, so a
value written on another node arrives with the next request rather than being pushed.

## The fallback chain

A read walks from the most specific scope to the least, then leaves the store entirely:

```
user-in-tenant → user → tenant → global → IConfiguration "Stratara:Settings:<name>" → code default
```

| Scope | What it answers |
|---|---|
| `SettingScope.ForUserInTenant(tenantId, userId)` | what this person chose in this workspace |
| `SettingScope.ForUser(userId)` | what this person chose everywhere |
| `SettingScope.ForTenant(tenantId)` | what the tenant's admin set for everyone in it |
| `SettingScope.Global` | the installation-wide answer |

`SettingScope` is a `readonly record struct` mirroring the security plane's `KeyScope`; ids are
strings, so slugs and `Guid`s both work. The configuration step lets an operator set an installation
default in `appsettings.json` without a database row.

## Settings that must not inherit

`IsInherited: false` consults **only the most specific scope** for the session — the flag for "answer
per user, or not at all". The nuance to internalize: with a session carrying both a tenant and a user,
the most specific scope is *user-in-tenant*, so a tenant-scope value is **not** seen at all.

The sample makes this concrete. `Ui.Density` is non-inherited, with `cozy` stored at the Acme tenant
scope and `compact` for Alice-in-Acme:

```
Alice @Acme   — Ui.Theme=dark,          Ui.Density=compact
Bob   @Acme   — Ui.Theme=high-contrast, Ui.Density=comfortable   // NOT Acme's cozy
Alice @Globex — Ui.Theme=system,        Ui.Density=comfortable
```

Bob has no density row of his own, so the read skips Acme's `cozy` entirely and lands on the code
default. `Ui.Theme` is inherited, so Bob *does* see Acme's `dark` — unless, as here, he overrode it.

## Encrypted settings at rest

`IsEncrypted: true` wraps the store in a transparent AES-GCM decorator over the security plane's
`ISecureBlobEncryptor`. The `KeyScope` is derived from the `SettingScope` (user → user-scoped key,
tenant → tenant-scoped, global → confidential) and the associated data binds the purpose to
`stratara:setting:<name>`. A leaked row therefore can neither be decrypted under another scope's key
nor replayed as a different setting. Plaintext definitions pass through untouched.

```csharp
builder.Services.AddStrataraBlobEncryption();   // Stratara.Security — required for IsEncrypted
```

## Why the defaults matter

Each of these is a failure the plane refuses to make quiet:

- **Reading an undeclared name throws** an `InvalidOperationException` from the provider. A typo
  surfaces at the first read instead of returning `null` and looking like an unset preference.
- **Declaring encrypted settings without an `ISecureBlobEncryptor` throws** at first resolution of the
  store — there is no fallback to plaintext. A secret is either encrypted or the host doesn't start
  serving.
- **Encrypted values are crypto-shreddable.** Because their keys live in `IKeyStore`,
  `EraseScopeAsync(scope)` renders a user's encrypted settings unreadable even before the rows are
  swept (GDPR Art. 17).

## Erasure

`DeleteScopeAsync` is the one operation that widens deliberately — every other read and write is
exact-scope:

```csharp
await store.DeleteScopeAsync(SettingScope.ForUser(userId));     // the user, across ALL tenants
await store.DeleteScopeAsync(SettingScope.ForTenant(tenantId)); // the tenant, across ALL users
await store.DeleteScopeAsync(SettingScope.Global);              // global values only
```

## See also

- The runnable `Stratara.Sample.IdentityDirectory` sample declares both flags and prints the fallback
  chain end to end; `InMemorySettingStore` (`Stratara.Testing`) is the test double.
- [Encrypt Sensitive Data](encrypt-data-setup.md) — the `IKeyStore` / `KeyScope` machinery `IsEncrypted` builds on.
- [Tenant Membership](tenant-membership.md) — the directory plane that shares these tables and feeds the session Subject.
