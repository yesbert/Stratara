## Context

See `proposal.md`, section Why. The relevant code, as of 4.3.0:

- `AddSettingCatalog` and `AddPermissionCatalog`
  (`src/Stratara.Identity.EntityFrameworkCore/DependencyInjection/IdentityDirectoryServiceCollectionExtensions.cs`)
  each build a new catalog, run the callback on it at once, and register it with `AddSingleton`.
  The callback runs at registration, which is why a duplicate setting name or an undeclared
  permission grant fails there. The last registration wins at resolution.
- `AddSettingStoreCore` registers `ISettingStore` and `ISettingProvider` as factories. Both call
  `GetRequiredService<SettingCatalog>()`. `ValidateOnBuild` checks the constructor dependencies of
  a type registration and does not look inside a factory. Nothing therefore reports a missing
  catalog before the first resolution.
- `SubjectEraser` takes `ISettingStore`. An erasure therefore resolves the store even for a host
  that has no settings to erase.
- `SettingCatalog.Add` throws on a duplicate name. `PermissionCatalog.Add` ignores a redeclaration,
  and `GrantToRole` accumulates and throws on an undeclared permission. Both types are sealed, public,
  and resolved directly as themselves by the stores, the resolvers and consumers.

## Goals / Non-Goals

**Goals:**

- The setting store and anything that depends on it resolve with no settings declared.
- A catalog declared in several calls is one catalog, in any order relative to the store.
- Declaration errors keep failing at registration, where they fail today.

**Non-Goals:**

- An empty default for the permission catalog. A permission resolver with no catalog would deny
  every check without a word, which is worse than today's failure at first resolution. Surfacing
  that failure at start-up is a separate question, and this change leaves it where it is.
- Merging catalogs that a consumer built and registered as separate singletons. The parts go
  through the two registration calls. A catalog instance registered directly is added to, not
  merged with another instance.

## Decisions

### The setting store brings an empty catalog when none is declared

`AddSettingStoreCore` registers an empty `SettingCatalog` instance with `TryAddSingleton`. A
catalog declared before the store is kept. A catalog declared after it is added to the same
instance, as the next decision describes.

*Alternative rejected: a start-up check that fails when the store is registered without a
catalog.* It moves the failure from the first erasure to the start, which is better. But it keeps a
composition that is legitimate, a host with nothing to declare, as an error, and the only way out is
the workaround the reporting consumer already wrote: declaring an empty catalog.

*Alternative rejected: have the eraser skip the setting sweep when no catalog exists.* It fixes the
one symptom that was reported and leaves the store unresolvable for everything else. It also puts
knowledge of the settings plane's registration into the eraser.

Evidence: the reported failure, and the example on `AddStrataraErasure`, which registers
`AddSettingStore<DirectoryDbContext>()` without a catalog.

### A declaration adds to the registered instance, at registration

`AddSettingCatalog` and `AddPermissionCatalog` look for the last registration of their catalog
type. They take a registration only when it is a non-keyed singleton with an implementation
instance, because that is the one resolution returns. When they find one, they run the callback on
that instance. When they find none, they create an instance, run the callback, and register it.
Errors in the callback surface in the call, as today.

*Alternative rejected: collect the callbacks and build the catalog at first resolution,* in the
manner of `IConfigureOptions<T>`. It composes more simply, but a duplicate name or an undeclared
grant would then fail at first resolution instead of at registration. That is the late failure this
change exists to remove.

*Alternative rejected: keep replacing, but throw on a second call.* It stops the silent loss, but
it makes the empty default from the first decision fail whenever the store is registered first,
and it forbids the per-module declaration that consumers are evidently writing.

*Alternative rejected: expose the catalog through `IOptions<T>`.* The stores, the resolvers and
consumers resolve `SettingCatalog` and `PermissionCatalog` directly, so this would be a breaking
change for a registration problem.

### A catalog registered as a factory makes the next declaration fail

When the last registration of the catalog type is a factory, or a type registration, there is no
instance to add to. The declaration then throws `InvalidOperationException` at registration. The
message says that the registered catalog was not created by `AddSettingCatalog` or
`AddPermissionCatalog` and cannot be added to.

*Alternative rejected: register the new part as before and let it win.* The consumer's catalog then
disappears silently, which is the defect.

*Alternative rejected: ignore the part.* The part's settings then disappear silently, which is the
same defect from the other side.

## Risks / Trade-offs

- **A host relied on a second call replacing the first.** → Nothing in the documentation suggests
  it, and the replaced declarations would have failed as undeclared on first use. A host doing this
  deliberately now finds out from a duplicate-name error or from the accumulated grants. The
  CHANGELOG entry names the change.
- **The catalog is mutated after it is registered.** → Only during registration, before the
  container is built. That is the lifetime the catalog's own documentation prescribes ("build the
  catalog completely during service registration").
- **Resolution with several instance registrations.** → The lookup takes the last registration,
  which is the one resolution returns. The earlier ones stay as dead registrations, as they are
  today.

## Migration Plan

Nothing to run. A host that works around the missing catalog by declaring an empty one can drop the
workaround after upgrading, and is not required to.
