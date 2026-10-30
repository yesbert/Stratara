# Share one identity store

> **Status:** approved

## Why

Several applications built on the framework can share one PostgreSQL server and are meant to share
one identity: one account per person, one membership per person and tenant. Today that cannot be
installed. The framework provides the identity model — ASP.NET Identity's tables with passkeys, and
the directory's memberships, selections, settings and API keys — but every host derives its own
context from it and generates its own migrations. ASP.NET Identity ships no migrations either. Three
hosts pointed at the same tables would run three migration lineages against them: the second host's
first migration creates tables that already exist and fails, and every later framework upgrade adds
the same column three times. Nothing keeps the hosts' models identical either — a host may extend the
user type with columns the others do not know.

The tables are defined by the framework and change when the framework is upgraded, so the one
migration lineage they need belongs to the framework.

## What Changes

The consumer-visible effect: a host that wants a shared identity store registers the framework's
identity store instead of its own identity context, and runs the migrations the framework ships.

- **A ready identity store with its migrations, on PostgreSQL.** It holds the ASP.NET Identity tables
  including passkeys and the directory's tables, in one schema with a fixed name, with its migration
  history inside that schema. The migrations are shipped in the package; a host generates none of its
  own for these tables.
- **Its location is configured on its own.** The store uses its own connection string, falling back
  to the host's default one, so hosts that keep their own databases can still meet in one identity
  database.
- **Every host applies the same migrations, one at a time.** Hosts that start together on an
  unmigrated store do not apply a migration twice; a host that finds a migration already applied by
  another moves on.
- **The account type is fixed** to ASP.NET Identity's standard user for this store, so no host can
  add columns the others do not know. A host that needs more about a person keeps it outside the
  identity store.
- **The schema changes only additively within a major version.** A host on an earlier release keeps
  working on a store a later release migrated. A change that is not additive is made only in a major
  version.
- **Moving an existing store is replacing it.** The documentation states how a host that has its own
  identity context moves: drop the old schema and its history table, register the shared store, run
  its migrations, and seed — with the same user and tenant ids as before, since event streams and
  encrypted data are bound to them. What a seed does not carry (passwords, authenticator keys,
  passkeys, API keys, encrypted settings) is gone, and the documentation says so.
- Hosting the identity and directory tables in a host's own context remains supported unchanged, for
  a host that does not share its store — including on a provider the shared store does not support.
  Moving such a store from another provider to PostgreSQL is the host's own work.

## Capabilities

### New Capabilities

- `identity-store`: one identity store that several hosts share — its migrations, its schema and
  location, migrating safely from several hosts, the fixed account shape, compatibility across
  releases, and how an existing store is replaced.

### Modified Capabilities

- `tenant-directory`: *The directory tables can be hosted in an existing context* names the shared
  identity store as the other way to host them.

## Impact

- `src/Stratara.EventSourcing.EntityFrameworkCore/IdentityStore/` — `StrataraIdentityStore.cs` (the shared store's
  context, beside `IdentityStore<TContext,TUser>`, with the directory model applied, the ASP.NET Identity schema
  version and key lengths fixed), `StrataraIdentityStoreOptionsBuilder.cs` (options and the design-time factory),
  and its `Migrations/` folder.
- `scripts/add-identity-store-migration.sh` — the one command that adds a migration to the store.
- `src/Stratara.EventSourcing.EntityFrameworkCore/EntityFrameworkCore/DependencyInjection/StrataraIdentityStoreServiceCollectionExtensions.cs`
  — registering the store with its own connection string and history table, and the migrate call.
- `src/Stratara.EventSourcing.EntityFrameworkCore/Stratara.EventSourcing.EntityFrameworkCore.csproj` — a
  reference to `Stratara.Identity.EntityFrameworkCore` for the directory model (both Tier-C, no cycle), and
  `Microsoft.EntityFrameworkCore.Design` with `PrivateAssets="all"` for generating the migrations.
- `tests/Stratara.Identity.IntegrationTests/` — new, Testcontainers on PostgreSQL; picked up by
  `integration.yml` without an edit; listed in `Stratara.slnx`.
  `tests/Stratara.EntityFrameworkCore.Tests/IdentityStore/StrataraIdentityStoreTests.cs` — model, migrations and
  registration without a database.
- `docs/guides/shared-identity-store.md` (new, in `toc.yml` and `index.md`) — the shared identity store and moving
  onto it; `tenant-membership.md` points at it; `docs/reference/di-extensions-cheatsheet.md` lists the
  registration. The `Stratara.EventSourcing.EntityFrameworkCore` README.
- `llms-full.txt` — the reference catalogue lists the store and its registration.
- `CHANGELOG.md`. New public types and registrations, nothing changed for a host that does not use
  them: a patch release.
