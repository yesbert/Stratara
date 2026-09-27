## 1. Refuse on a read-only collection

- [x] 1.1 `GetOrAddCatalog` refuses before looking up the catalog
  (`src/Stratara.Identity.EntityFrameworkCore/DependencyInjection/IdentityDirectoryServiceCollectionExtensions.cs`);
  `AddPermissionCatalog` and `AddSettingCatalog` document the exception
- [x] 1.2 `AddMembershipAuthorizationOptions` refuses the same way, and so do the `AddMembershipAuthorization`
  overloads through it; documented
- [x] 1.3 `TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver` refuses
  (`src/Stratara.Abstractions/Abstractions/Reflections/TrustedTypeResolverServiceCollectionExtensions.cs`); its public
  callers there document the exception
- [x] 1.4 The Orleans registrations that reach `RolePlacement.Publish` and `SingletonWorkRegistrations.Of` refuse at
  their entry, before they register anything else, so the error is theirs rather than the collection's
  (`src/Stratara.Orleans/Hosting/BuiltHostGuard.cs`, called from `AddStrataraAggregateGrains`,
  `AddStrataraProjectionGrains`, `AddStrataraSagaGrains`, `AddStrataraDurableTimers`, `AddStrataraSingletonWork`);
  documented on each

## 2. Tests

- [x] 2.1 Catalogs and membership options: a built host, then the registration → `InvalidOperationException` naming
  it, catalog unchanged; a registration before the build still adds to earlier parts
  (`tests/Stratara.Identity.EntityFrameworkCore.Tests/RegistrationAfterBuildTests.cs`)
- [x] 2.2 Trusted types: the same for `AddTrustedType<T>` and `AddProjectionsFromAssemblyContaining<T>`, resolver
  unchanged (`tests/Stratara.Projections.Tests/DependencyInjection/TrustedTypeRegistrationAfterBuildTests.cs`)
- [x] 2.3 Orleans: every one of the five registrations after `HostApplicationBuilder.Build()` fails naming itself,
  and before it succeeds twice over (`tests/Stratara.Orleans.Tests/RegistrationAfterBuildTests.cs`)
- [x] 2.4 Counter-check: with `src/` as on `main`, the refusal tests of 2.1–2.3 fail (3 + 1 + 5)

## 3. Housekeeping

- [x] 3.1 S4136: `AddMembershipAuthorizationOptions` moved below the generic `AddMembershipAuthorization<TUser>`
  overloads, no other change in the move
- [x] 3.2 `CHANGELOG.md` under `[4.4.0]` → Fixed

## 4. Verification

- [x] 4.1 `./scripts/local-gauntlet.sh` green
- [x] 4.2 `openspec validate refuse-a-registration-after-the-host-is-built --strict`
- [x] 4.3 Independent review before the merge
