## 1. The store

- [x] 1.1 `src/Stratara.EventSourcing.EntityFrameworkCore/Stratara.EventSourcing.EntityFrameworkCore.csproj`:
  project reference to `Stratara.Identity.EntityFrameworkCore`; the tier check in
  `tests/Stratara.Documentation.Tests` (or wherever tier order is asserted) stays green.
- [x] 1.2 `src/Stratara.EventSourcing.EntityFrameworkCore/IdentityStore/StrataraIdentityStore.cs`:
  sealed, `IdentityStore<StrataraIdentityStore, IdentityUser>`, applies the directory model, default
  schema `stratara_identity`. XML docs state the fixed schema, the history table and that the user
  type cannot be extended.
- [x] 1.3 One internal options-building method (Npgsql, snake case, history table
  `__stratara_identity_migrations` in `stratara_identity`) used by the runtime registration and by an
  internal `IDesignTimeDbContextFactory<StrataraIdentityStore>`.
- [x] 1.4 Generate the initial migration into `IdentityStore/Migrations/` with
  `scripts/add-identity-store-migration.sh`. Unit test in
  `tests/Stratara.EntityFrameworkCore.Tests` (or the project that covers this package): the model has
  no pending changes against the latest migration.
- [x] 1.5 Unit test, same project: every migration after the first contains no drop, rename or alter
  of an existing table, column or index.

## 2. Registration and migration

- [x] 2.1 `AddStrataraIdentityStore()` in
  `src/Stratara.EventSourcing.EntityFrameworkCore/EntityFrameworkCore/DependencyInjection/`: context
  factory with connection `identitydb` falling back to `IDbResolver`; scoped context; the directory
  stores from the context factory. Registration tests: the context resolves; `identitydb` wins over
  `defaultdb`; without it `defaultdb` is used; `ITenantMembershipStore`, `IApiKeyStore`,
  `ISettingStore` resolve on it.
- [x] 2.2 `AddAspNetIdentity<IdentityUser, StrataraIdentityStore>()` works unchanged — covered by every test in
  `tests/Stratara.Identity.IntegrationTests/SharedIdentityStoreTests.cs`, whose hosts register accounts that way
  (`Stratara.Identity.AspNetCore.Tests` does not reference the PostgreSQL package).
- [x] 2.3 `MigrateStrataraIdentityStoreAsync(this IServiceProvider)`: create the database if missing
  (an "already exists" race counts as success), then `MigrateAsync`.

## 3. Integration on PostgreSQL

- [x] 3.1 New `tests/Stratara.Identity.IntegrationTests/` on Testcontainers PostgreSQL (picked up by
  `integration.yml`).
- [x] 3.2 Empty database: migrate; all tables and the history in `stratara_identity`, no other schema
  touched; create an account, sign in with a password, register and use a passkey, write a membership,
  a selection, a setting and an API key.
- [x] 3.3 Second host on a migrated store applies nothing and uses the store.
- [x] 3.4 Two hosts migrate an empty store concurrently: both complete, each migration recorded once.
  Passes with EF Core's migration lock; no advisory lock needed.
- [x] 3.5 Earlier-release host on a later-migrated store: apply a test-only additive migration
  (a nullable column, a table) on top of the shipped ones, then run the shipped migrate call and
  sign-in, membership read and account write — nothing fails.
- [x] 3.6 Two hosts with different default connections and the same `identitydb` see the same
  accounts and memberships.

## 4. Documentation and release notes

- [x] 4.1 `docs/guides/shared-identity-store.md` (new, linked from `docs/guides/toc.yml` and `index.md`): when to use it, the
  registration, the migrate call, the fixed schema and history table, the connection, snake-case table
  names, additive-only within a major version, moving onto it (drop, register, migrate, seed with the
  same user and tenant ids; what is lost). `docs/guides/tenant-membership.md` points at it.
- [x] 4.2 `src/Stratara.EventSourcing.EntityFrameworkCore/README.md`, `llms-full.txt`: the store, its
  registration and migrate call.
- [x] 4.3 `CHANGELOG.md`: Added — the shared identity store.
- [x] 4.4 `./scripts/local-gauntlet.sh` green; `dotnet test tests/Stratara.Identity.IntegrationTests`
  green; `openspec validate share-one-identity-store --strict`.
