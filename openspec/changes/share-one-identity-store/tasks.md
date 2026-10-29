## 1. The store

- [ ] 1.1 `src/Stratara.EventSourcing.EntityFrameworkCore/Stratara.EventSourcing.EntityFrameworkCore.csproj`:
  project reference to `Stratara.Identity.EntityFrameworkCore`; the tier check in
  `tests/Stratara.Documentation.Tests` (or wherever tier order is asserted) stays green.
- [ ] 1.2 `src/Stratara.EventSourcing.EntityFrameworkCore/IdentityStore/StrataraIdentityStore.cs`:
  sealed, `IdentityStore<StrataraIdentityStore, IdentityUser>`, applies the directory model, default
  schema `stratara_identity`. XML docs state the fixed schema, the history table and that the user
  type cannot be extended.
- [ ] 1.3 One internal options-building method (Npgsql, snake case, history table
  `__stratara_identity_migrations` in `stratara_identity`) used by the runtime registration and by an
  internal `IDesignTimeDbContextFactory<StrataraIdentityStore>`.
- [ ] 1.4 Generate the initial migration into `IdentityStore/Migrations/`. Unit test in
  `tests/Stratara.EntityFrameworkCore.Tests` (or the project that covers this package): the model has
  no pending changes against the latest migration.
- [ ] 1.5 Unit test, same project: every migration after the first contains no drop, rename or alter
  of an existing table, column or index.

## 2. Registration and migration

- [ ] 2.1 `AddStrataraIdentityStore()` in
  `src/Stratara.EventSourcing.EntityFrameworkCore/EntityFrameworkCore/DependencyInjection/`: context
  factory with connection `identitydb` falling back to `IDbResolver`; scoped context; the directory
  stores from the context factory. Registration tests: the context resolves; `identitydb` wins over
  `defaultdb`; without it `defaultdb` is used; `ITenantMembershipStore`, `IApiKeyStore`,
  `ISettingStore` resolve on it.
- [ ] 2.2 `AddAspNetIdentity<IdentityUser, StrataraIdentityStore>()` works unchanged; a test in
  `tests/Stratara.Identity.AspNetCore.Tests/DependencyInjection/` that the user store resolves over the
  shared context.
- [ ] 2.3 `MigrateStrataraIdentityStoreAsync(this IServiceProvider)`: create the database if missing
  (an "already exists" race counts as success), then `MigrateAsync`.

## 3. Integration on PostgreSQL

- [ ] 3.1 New `tests/Stratara.Identity.IntegrationTests/` on Testcontainers PostgreSQL (picked up by
  `integration.yml`).
- [ ] 3.2 Empty database: migrate; all tables and the history in `stratara_identity`, no other schema
  touched; create an account, sign in with a password, register and use a passkey, write a membership,
  a selection, a setting and an API key.
- [ ] 3.3 Second host on a migrated store applies nothing and uses the store.
- [ ] 3.4 Two hosts migrate an empty store concurrently: both complete, each migration recorded once.
  If it fails on the pinned Npgsql, add the advisory lock around `MigrateAsync` (design) and keep the
  test.
- [ ] 3.5 Earlier-release host on a later-migrated store: apply a test-only additive migration
  (a nullable column, a table) on top of the shipped ones, then run the shipped migrate call and
  sign-in, membership read and account write — nothing fails.
- [ ] 3.6 Two hosts with different default connections and the same `identitydb` see the same
  accounts and memberships.

## 4. Documentation and release notes

- [ ] 4.1 `docs/guides/shared-identity-store.md` (new, linked from the DocFX toc): when to use it, the
  registration, the migrate call, the fixed schema and history table, the connection, snake-case table
  names, additive-only within a major version, moving onto it (drop, register, migrate, seed with the
  same user and tenant ids; what is lost). `docs/guides/tenant-membership.md` points at it.
- [ ] 4.2 `src/Stratara.EventSourcing.EntityFrameworkCore/README.md`, `llms-full.txt`: the store, its
  registration and migrate call.
- [ ] 4.3 `CHANGELOG.md`: Added — the shared identity store.
- [ ] 4.4 `./scripts/local-gauntlet.sh` green; `dotnet test tests/Stratara.Identity.IntegrationTests`
  green; `openspec validate share-one-identity-store --strict`.
