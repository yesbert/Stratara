## Context

See proposal.md — Why. The pieces that exist:

- `IdentityStore<TContext, TUser>` in `Stratara.EventSourcing.EntityFrameworkCore/IdentityStore/` — an
  `IdentityDbContext<TUser>` that adds the `AspNetUserPasskeys` table with JSON credential data. A
  host derives its own context from it.
- `ApplyIdentityDirectoryModel()` in `Stratara.Identity.EntityFrameworkCore` — the directory's four
  tables, `ToTable` without a schema, so they land in the host context's default schema.
- `AddNpgsqlIdentityDbContextFactory<TDbContext>()` — registers a host's identity context on Npgsql
  with `UseSnakeCaseNamingConvention()`, the connection from `IDbResolver` (`defaultdb`), and nothing
  about migrations.
- `DbContextMigrationUtility.MigrateAllDatabasesAsync` — creates the database if missing and calls
  `MigrateAsync`.
- The framework ships no migration anywhere. Every host generates its own, in a schema and with a
  history table of its own choosing. Evidence: the implementation.

Owner decisions, 2026-10-29, in the planning conversation: the framework owns the migrations; the
shared store is PostgreSQL only — another provider and moving from one stays the host's; the schema
name is fixed, not configurable; existing stores are replaced and re-seeded, not converted.

## Goals / Non-Goals

**Goals:**
- One migration lineage, shipped in the package, applied unchanged by every host.
- Nothing changes for a host that keeps its own identity context.

**Non-Goals:**
- A configurable schema name (owner decision). Only the database is configurable.
- Any provider other than PostgreSQL, and converting a store from one.
- Converting an existing store into the shared one.
- Sharing anything beyond the store — cookies, Data Protection keys, a token service. Each host signs
  in on its own.

## Decisions

**The store lives in `Stratara.EventSourcing.EntityFrameworkCore`.** It is already the PostgreSQL
package that ships `IdentityStore<,>` and the Npgsql registrations. It gains a project reference to
`Stratara.Identity.EntityFrameworkCore` for `ApplyIdentityDirectoryModel` — both Tier-C, and
`Stratara.Identity.EntityFrameworkCore` references only Tier-A, so no cycle.
- *Alternative:* a new package `Stratara.Identity.PostgreSql`. Rejected: a 29th package, a new entry
  in the publish filter and in every lockstep statement, for one context and its migrations.

**A sealed context with the standard user.** `public sealed class StrataraIdentityStore :
IdentityStore<StrataraIdentityStore, IdentityUser>` (name to be settled in review), whose
`OnModelCreating` calls the base, then `ApplyIdentityDirectoryModel()`, then
`HasDefaultSchema("stratara_identity")`. Sealed and non-generic is how "no host adds columns" is
enforced: there is nothing to derive from or to parameterise.

**Migrations are generated in the repository and shipped in the assembly.** Under
`IdentityStore/Migrations/`, created with `dotnet ef migrations add` against an internal
`IDesignTimeDbContextFactory<StrataraIdentityStore>` that builds the options the same way the runtime
registration does (Npgsql, snake case). One shared options-building method serves both, so design time
and run time cannot diverge. The history table is `__stratara_identity_migrations` in
`stratara_identity`, set through `MigrationsHistoryTable` in that same method.
- *Alternative:* idempotent hand-written SQL scripts. Rejected: a second, unchecked description of a
  model EF already describes, and no history a later release can build on.

**The model does not follow the host's `IdentityOptions.Stores`.** Found during implementation: ASP.NET
Identity builds its model from the host's store options — the schema version (which adds or omits the passkey
table) and `MaxLengthForKeys`, which also sets the phone-number length. The design-time factory sees none, the
framework's identity registration sets `SchemaVersion = Version3`, so the first shipped migration did not match any
host (`PendingModelChangesWarning` at the migrate call, in every integration test). The store therefore overrides
`SchemaVersion` with `IdentitySchemaVersions.Version3` and sets the login/token key lengths to 128 and the phone
number to 256 — the values the framework's registration produces — so the model is the same in every host and at
design time. A host that changes an option the model depends on beyond those (personal-data protection) still gets
a model the migrations do not describe, and the migrate call fails loudly. Evidence:
`SharedIdentityStoreTests` (all five fail without the overrides),
`StrataraIdentityStoreTests.The_model_has_no_change_the_latest_shipped_migration_lacks`.

**Generating a migration is one script.** `scripts/add-identity-store-migration.sh <Name>` runs
`dotnet ef migrations add` with the context, output folder and namespace, moves the model snapshot that EF places by
namespace back next to the migrations, and qualifies the generated base class: inside
`Stratara.EventSourcing.EntityFrameworkCore`, `Migration` resolves to the namespace of `DbContextMigrationUtility`.
The package references `Microsoft.EntityFrameworkCore.Design` with `PrivateAssets="all"`, so it is not a consumer
dependency.

**Its own connection, falling back to the default.** The registration resolves the connection string
`identitydb` and falls back to the one `IDbResolver` resolves (`defaultdb`). Options are not tenant-
routed: the identity store is one database for all tenants.

**One registration, one migrate call.** `AddStrataraIdentityStore()` registers the context factory
and a scoped context (so `AddAspNetIdentity<IdentityUser, StrataraIdentityStore>()` resolves it), and
the directory's stores over the factory with a context per operation
(`AddTenantMembershipStoreFromContextFactory`, `AddApiKeyStoreFromContextFactory`,
`AddSettingStoreFromContextFactory`). `MigrateStrataraIdentityStoreAsync(this IServiceProvider)`
creates the database if missing and applies the migrations; a host calls it at start, as it already
does for its own contexts.

**The lock is EF Core's, verified, with a fallback ready.** Since EF Core 9, `MigrateAsync` takes a
database-wide lock before applying migrations, and Npgsql implements it. That is the mechanism the
requirement relies on; the integration test that runs two hosts' migrations at once is the evidence.
Should it fail on the pinned Npgsql, the migrate call takes a PostgreSQL advisory lock around
`MigrateAsync` instead — the requirement does not change. Verified: the concurrent test passes on Npgsql 10 with
EF Core's lock alone, so no advisory lock was added. Creating the database is not covered by
that lock; a concurrent "already exists" from `CreateAsync` is treated as success.

**Additivity is checked by a test, not by review.** A unit test walks every shipped migration after the
first and fails on any operation that drops, renames or alters an existing table, column or index.
Changing that test is what a major version's breaking schema change looks like in a diff. A second
test asserts the model has no pending changes against the latest migration, so a framework change that
alters the model without a migration fails in CI.

**Moving onto the store is documentation.** A guide under `docs/guides/` states the replace-and-seed
steps, the identifiers to keep, what is lost, and what the host does with its own former identity
context (delete the context and its migrations). No code, by owner decision.

## Risks / Trade-offs

- [A host registers the store with options of its own (another naming convention)] → the model no
  longer matches the shipped migrations; EF Core's pending-model-changes check fails the migrate call
  naming the context. The registration offers no hook to change the conventions.
- [An ASP.NET Identity release changes its model] → the framework ships the migration with the package
  upgrade. If that migration is not additive, the upgrade waits for a major version or the model change
  is suppressed in the context; the additivity test makes the choice explicit.
- [Hosts on two major versions share a store] → not supported, and stated: within a major version
  only.
- [Snake case makes the ASP.NET Identity tables `asp_net_users` etc.] → the same as hosts get today
  through `AddNpgsqlIdentityDbContextFactory`; documented in the guide.

## Migration Plan

Additive: nothing changes for a host that does not register the store. A host that moves follows the
guide: drop its old identity schema and history table, swap its identity context for the shared store
(`AddAspNetIdentity<IdentityUser, StrataraIdentityStore>()`, `AddStrataraIdentityStore()`, and the
directory registrations it had removed), call `MigrateStrataraIdentityStoreAsync()`, seed. Rollback is
the reverse with the host's own seed.
