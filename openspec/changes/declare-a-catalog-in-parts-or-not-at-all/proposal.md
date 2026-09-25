# Declare a catalog in parts, or not at all

> **Status:** approved

## Why

A consumer on 4.3.0 registered the setting store and subject erasure and declared no settings,
because it has none. The container built, passed `ValidateOnBuild` and every start-up check. Then
the first erasure failed with *No service for type `SettingCatalog` has been registered*. The
command that carried it was retried three times and dead-lettered, so a deletion the platform had
accepted was dropped. The store resolves the catalog inside a factory, which build-time validation
cannot see. The catalog is registered only by `AddSettingCatalog`, and nothing says that call is
required. The framework's own example for `AddStrataraErasure` registers the setting store without
it, and fails the same way.

The same registration has a second fault, and so does its sibling. `AddSettingCatalog` and
`AddPermissionCatalog` each register a new catalog on every call, and the last one wins. A host that
declares its settings or its permissions in two places, one per module, silently keeps only the last
module's. The first module's settings then fail as undeclared. Its permissions vanish, and every
check against them is denied.

## What Changes

- The setting store works without declared settings. Registering it provides an empty setting
  catalog when none has been declared. The store then resolves and works, erasure included, and
  reading any setting fails as undeclared, as it does today for a name the catalog lacks.
- `AddSettingCatalog` adds to the catalog already registered instead of replacing it. This holds in
  whichever order it and the store registration run. A setting name declared in two parts fails, as
  a name declared twice in one part does today.
- `AddPermissionCatalog` adds to the permission catalog already registered, in the same way.
  Redeclaring a permission has no effect, and grants to one role accumulate across parts, as the
  permission catalog already promises within one declaration.
- A catalog registered by other means than these calls, as a factory, cannot be added to. The next
  `AddSettingCatalog` or `AddPermissionCatalog` fails at registration with a message saying so,
  instead of replacing it or being ignored.
- The documentation of the setting store says the catalog is optional. The erasure example becomes
  one that runs.

A host that calls each catalog registration once is unaffected. No signature changes.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `scoped-settings`: the vocabulary requirement gains declaration in parts and a store that stands
  without declared settings.
- `authorization`: the vocabulary requirement gains declaration in parts.

## Impact

- `src/Stratara.Identity.EntityFrameworkCore/DependencyInjection/IdentityDirectoryServiceCollectionExtensions.cs`:
  `AddSettingCatalog`, `AddPermissionCatalog` and `AddSettingStoreCore`, plus the XML documentation
  of both store registrations.
- `src/Stratara.Infrastructure/DependencyInjection/ErasureServiceCollectionExtensions.cs`: the
  example.
- `tests/Stratara.Identity.EntityFrameworkCore.Tests`: registration-order, two-part and
  no-catalog cases; and an erasure over a store with no declared settings.
- `docs/guides/scoped-settings.md`, `docs/guides/require-permission.md`,
  `docs/reference/di-extensions-cheatsheet.md`.
- `CHANGELOG.md`.
- Nothing is dissolved or superseded by this change.
