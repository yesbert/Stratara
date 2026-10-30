---
title: "Shared Identity Store"
description: "One identity store several hosts share on PostgreSQL: accounts, passkeys and the tenant directory in a fixed schema, created and evolved by migrations the framework ships."
---

# Share One Identity Store

> **Derived page.** The behaviour described here is specified by the `identity-store` capability under
> `openspec/specs/`. That specification is the source; this page explains and illustrates it. Where the two
> disagree, the specification is right and this page is a bug.

Several applications built on Stratara can share one PostgreSQL server and one identity: one account per person,
one membership per person and tenant. `StrataraIdentityStore`, in `Stratara.EventSourcing.EntityFrameworkCore`,
is the store they share. It holds ASP.NET Identity's tables — passwords, authenticator keys, recovery codes,
passkeys, external logins, claims, tokens, roles — and the tenant directory: memberships, tenant selections,
settings and API keys. **The migrations that create and evolve it ship in the package.** A host that uses it
generates none of its own for these tables.

A host that does not share its identity can go on hosting the tables in a context of its own — see
[Hosting the directory tables](tenant-membership.md#hosting-the-directory-tables). The shared store exists because
that does not work for two hosts on the same tables: each host's own migrations would create tables the other
already created, and every framework upgrade would add the same column once per host.

## Register it and migrate it

<!-- stratara-snippet-ignore: narrative fragment - the host builder comes from the surrounding text -->
```csharp
builder.Services.AddStrataraIdentityStore();                              // the store and the directory's stores
builder.AddAspNetIdentityWithSignInManager<IdentityUser, StrataraIdentityStore>();
builder.Services.AddCheckedMembershipTenantClaim<IdentityUser>();         // see below

var app = builder.Build();
await app.Services.MigrateStrataraIdentityStoreAsync();                   // at start, in every host
```

`AddStrataraIdentityStore()` registers the store on PostgreSQL and the directory's stores over it —
`ITenantMembershipStore`, `IApiKeyStore`, `ISettingStore` — each with a context of its own per operation. Do not
also register them over a context of your own: the accounts and the memberships would end up in different places.

`MigrateStrataraIdentityStoreAsync()` creates the database where it does not exist yet and applies the shipped
migrations it has not applied. Every host applies the same migrations, identified the same way, so a migration one
host applied is recognised by every other. **Hosts that start at the same time take turns**: EF Core's migration
lock makes the second wait for the first, and nothing is applied twice.

## Where it lives

| | |
|---|---|
| Schema | `stratara_identity`, fixed — the same for every host and every release |
| Migration history | `stratara_identity.__stratara_identity_migrations`, apart from any history your own contexts keep |
| Connection | the connection string `identitydb`; where it is absent, `defaultdb` — read from configuration, never through a tenant-routing `IDbResolver` |
| Table names | ASP.NET Identity's `AspNet…` names, with snake-case columns; the directory's `tenant_membership`, `active_tenant`, `setting_entry`, `api_key` |

The separate connection is what lets hosts keep their own databases and still meet in one identity database: give
every host the same `identitydb`.

```json
{
  "ConnectionStrings": {
    "defaultdb": "Host=db;Database=billing;Username=billing;Password=…",
    "identitydb": "Host=db;Database=identity;Username=identity;Password=…"
  }
}
```

## The account is the standard one

The store's account type is ASP.NET Identity's `IdentityUser`, and there is no way to add columns to it: every
host sharing the store has to see the same columns. Keep what you know about a person beyond the account — a
display name, an avatar, preferences — outside the identity store, for instance as data the person owns alone
(see [Data that belongs to a person](encrypt-data-setup.md#data-that-belongs-to-a-person)).

The store also fixes the parts of the ASP.NET Identity schema that would otherwise follow each host's
`IdentityOptions.Stores`: the schema version (version 3, with passkeys) and the key and phone-number lengths, so
every host's model matches the shipped migrations whatever it configures there. A host that turns on
`Stores.ProtectPersonalData` is refused at start: it would write account columns the other hosts cannot read or
match.

## One selection, several applications

A user has one tenant selection across everything that shares the store. With live per-request resolution, a
switch in one application would move every open session the user has in all of them. **Use
`AddCheckedMembershipTenantClaim<TUser>()`** in every host that shares the store: the tenant is stamped at
sign-in, kept across a cookie refresh, and checked against the membership on every request — see
[The sign-in tenant claim](tenant-membership.md#the-sign-in-tenant-claim).

## Role names and keys

Role names are global in the store. Give each application's roles a prefix of its own so that two applications
never mean different things by one role. The framework enforces no form; a form that works is
`<scope>.<role>`:

- all lowercase, exactly one dot, after the scope; words within the role joined by a hyphen
  (`billing.invoice-approver`);
- the role a noun for who holds it, not a verb for what it allows;
- one scope per application, plus two shared ones: `platform.` for the operator of the installation and `tenant.`
  for roles that mean the same in every application within one tenant (`tenant.owner`).

An application then grants nothing for a role outside its own scope and those two, so a role another application
introduces later cannot widen what a member may do in it.

**API keys are shared the same way.** The `api_key` table holds the keys of every application, and
`IApiKeyStore.GetForTenantAsync` lists all of a tenant's keys, whichever application issued them; `RevokeAsync`
revokes any of them. A key administration that should show only its own application's keys filters the
descriptors itself — by the scopes of their `Roles`, for a machine key. A personal access token carries no roles
of its own (it acts with the roles of the user's membership, in every application), so a filter by role hides
every token: decide separately whether your key administration shows them.

## Across releases

Within a major version, the store's schema changes **only additively**: new tables, new indexes, new columns that
are nullable or have a database default. Nothing a release created is renamed, dropped or retyped. A host on an
earlier release of the same major version therefore keeps working on a store a later release migrated: it applies
nothing, and ignores the migrations it does not know. A change that is not additive is made only in a major
version and listed as a breaking change — and hosts sharing a store move to that major version together.

## Moving an existing identity store onto it

The framework does not convert an identity store you kept in your own context. Moving is replacing:

1. **Remove the old store's tables and its migration history** — the schema your identity context used, and its
   history table.
2. **Register the shared store** as above, remove your own identity context and its migrations, and run
   `MigrateStrataraIdentityStoreAsync()`.
3. **Seed it.** Recreate users and tenants **with the ids they had**. Event streams record their owners by those
   ids, and encrypted data is bound to them: a user seeded under a new id owns none of their streams and can read
   none of their encrypted fields.

Whatever the seed does not recreate is gone: password hashes, authenticator keys, recovery codes, passkeys, API
keys, and settings — encrypted settings included. For an installation with real accounts, decide before you move
what each of those means for its users.
