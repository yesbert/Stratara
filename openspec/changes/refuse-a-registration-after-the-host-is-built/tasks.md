## 1. Refuse on a read-only collection

- [ ] 1.1 `GetOrAddCatalog` refuses before looking up the catalog
  (`src/Stratara.Identity.EntityFrameworkCore/DependencyInjection/IdentityDirectoryServiceCollectionExtensions.cs`);
  `AddPermissionCatalog` and `AddSettingCatalog` document the exception
- [ ] 1.2 `AddMembershipAuthorizationOptions` refuses the same way, and so do the `AddMembershipAuthorization`
  overloads through it; documented
- [ ] 1.3 `TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver` refuses
  (`src/Stratara.Abstractions/Abstractions/Reflections/TrustedTypeResolverServiceCollectionExtensions.cs`); its public
  callers there document the exception
- [ ] 1.4 `RolePlacement.Publish` and `SingletonWorkRegistrations.Of` refuse (`src/Stratara.Orleans/Hosting/RolePlacement.cs`,
  `src/Stratara.Orleans/Singleton/SingletonWorkRegistrations.cs`); the public registrations that reach them document it

## 2. Tests

- [ ] 2.1 Catalogs and membership options: a built host, then the registration → `InvalidOperationException` naming
  it, catalog unchanged (`tests/Stratara.Identity.EntityFrameworkCore.Tests`); a registration before the build still
  adds to earlier parts
- [ ] 2.2 Trusted types: the same for `AddTrustedType<T>`, resolver unchanged
- [ ] 2.3 Orleans: the same for `AddStrataraSingletonWork` and one grain registration (`tests/Stratara.Orleans.Tests`)
- [ ] 2.4 Counter-check: without the guard, 2.1–2.3 fail

## 3. Housekeeping

- [ ] 3.1 S4136: `AddMembershipAuthorizationOptions` moved below the generic `AddMembershipAuthorization<TUser>`
  overloads, no other change in the move
- [ ] 3.2 `CHANGELOG.md` under `[4.4.0]` → Fixed

## 4. Verification

- [ ] 4.1 `./scripts/local-gauntlet.sh` green
- [ ] 4.2 `openspec validate refuse-a-registration-after-the-host-is-built --strict`
- [ ] 4.3 Independent review before the merge
