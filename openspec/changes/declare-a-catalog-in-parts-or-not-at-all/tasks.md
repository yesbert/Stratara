## 1. The setting store stands without declared settings

- [x] 1.1 In `AddSettingStoreCore`
  (`src/Stratara.Identity.EntityFrameworkCore/DependencyInjection/IdentityDirectoryServiceCollectionExtensions.cs`),
  register an empty `SettingCatalog` instance with `TryAddSingleton`.
- [x] 1.2 Update the XML documentation of `AddSettingStore<TContext>` and
  `AddSettingStoreFromContextFactory<TContext>`. The catalog is optional; without one, the store
  works and reading any setting through the provider fails as undeclared.
- [x] 1.3 In `tests/Stratara.Identity.EntityFrameworkCore.Tests/SettingCatalogRegistrationTests.cs`, add a test that builds a
  provider with `ValidateOnBuild` and only the store registered. It resolves `ISettingStore` and
  `ISettingProvider`, removes a user scope's settings, and asserts that reading any name fails as
  undeclared.

## 2. A catalog is declared in parts

- [x] 2.1 Add one private helper to the same file. It takes the last non-keyed registration of a
  catalog type and does one of three things:
  - returns its implementation instance;
  - when there is no registration, creates and registers a new instance;
  - when the registration is a factory or a type registration, throws `InvalidOperationException`
    naming the registration call.
- [x] 2.2 Route `AddSettingCatalog` through it, running the callback on the returned instance.
  Update its XML documentation: parts add up, a duplicate name across parts fails, and the order
  relative to the store does not matter.
- [x] 2.3 Route `AddPermissionCatalog` through it in the same way. Update its XML documentation:
  parts add up, grants accumulate, and a grant is checked against the permissions declared so far.
- [x] 2.4 Add `SettingCatalogRegistrationTests.cs` beside `SettingCatalogTests.cs`, with registration-level cases: two parts before and after
  the store, a duplicate name across parts, and a catalog registered as a factory.
- [x] 2.5 Add `PermissionCatalogRegistrationTests.cs` beside `CatalogPermissionResolverTests.cs`, with the
  catalog declared in two parts. Assert that a role's grants accumulate, that a second part may
  grant a permission the first part declared, and that a factory registration makes the next part
  fail.

## 3. Documentation

- [x] 3.1 Fix the example on `AddStrataraErasure`
  (`src/Stratara.Infrastructure/DependencyInjection/ErasureServiceCollectionExtensions.cs`). It
  runs after this change, and it should say that the setting store needs no declared settings.
- [x] 3.2 In `docs/guides/scoped-settings.md`, say that the vocabulary may be declared in parts and
  that the store works without one. In `docs/guides/require-permission.md`, say the same about
  parts. Update the two catalog rows of `docs/reference/di-extensions-cheatsheet.md`.
- [x] 3.3 Add the entry to `CHANGELOG.md` under Unreleased.

## 4. Gate

- [x] 4.1 `openspec validate declare-a-catalog-in-parts-or-not-at-all --strict`
- [x] 4.2 `./scripts/local-gauntlet.sh`
