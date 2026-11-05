# Tasks

## 1. Prove the compare-and-swap translates

- [ ] 1.1 In `tests/Stratara.Identity.EntityFrameworkCore.Tests`, write a test that runs
  `ExecuteUpdateAsync` filtered by `Roles == <list>` against the SQLite directory fixture. It must
  affect one row when the stored roles match and none when they differ. Run the same check once on
  PostgreSQL in `tests/Stratara.Identity.IntegrationTests` (`SharedIdentityStoreTests` fixture). If
  either fails, switch to the raw-SQL fallback in design.md D1/Risks before continuing.

## 2. The contract and its fallback

- [ ] 2.1 Add `AddRolesAsync` and `RemoveRolesAsync` to `ITenantMembershipStore`
  (`src/Stratara.Abstractions/Abstractions/Multitenancy/ITenantMembershipStore.cs`) as default
  interface methods, implemented as get + whole set with the D3 semantics. Their XML docs state that
  this fallback gives no protection against concurrent writers, and that whole writes and single-role
  changes do not protect each other. Verify with a test in `tests/Stratara.Testing.Tests` (there is no
  Abstractions test project): a minimal hand-written store that does not override
  the members compiles, adds and removes roles, and returns `null` for a missing membership.

## 3. The in-memory double

- [ ] 3.1 Implement both members on `InMemoryTenantMembershipStore` (`src/Stratara.Testing`) under
  `_gate`. Verify in `tests/Stratara.Testing.Tests/InMemoryTenantMembershipStoreTests.cs` with tests
  for: adding keeps the other roles and the status, adding a held role does not duplicate it, removing
  keeps the others, a missing membership returns `null` and creates nothing, a pending membership
  stays pending, and 50 parallel additions of distinct roles all survive.

## 4. The EF store

- [ ] 4.1 Implement both members on `EfTenantMembershipStore` with the compare-and-swap loop from
  D1/D2: 5 attempts, then `InvalidOperationException` naming the user and the tenant. Verify in
  `tests/Stratara.Identity.EntityFrameworkCore.Tests/EfTenantMembershipStoreTests.cs` with the same
  semantics cases as 3.1, plus one test that injects a concurrent write between the read and the
  update (via a second context) and shows that both changes survive.
- [ ] 4.2 Show that the shared-context mode does not flush unsaved consumer changes: in
  `DirectoryContextSharingTests`, add an unsaved entity to the shared context, call `AddRolesAsync`,
  and assert that the entity is still unsaved.
- [ ] 4.3 Show that two concurrent additions of different roles to one membership both survive on
  PostgreSQL through the shared identity store, as an integration test in
  `tests/Stratara.Identity.IntegrationTests/SharedIdentityStoreTests.cs` (this is NextPA finding
  F-033's own test).
- [ ] 4.4 Verify that the model is unchanged: `dotnet ef migrations has-pending-model-changes` (or the
  existing pending-model-changes test) on the shared identity store reports nothing, and no migration
  is added under `src/Stratara.EventSourcing.EntityFrameworkCore/IdentityStore`.

## 5. Documentation

- [ ] 5.1 Add a section "Changing single roles" to `docs/guides/tenant-membership.md`. It shows
  `AddRolesAsync`/`RemoveRolesAsync`, says that they are safe against concurrent role changes on the
  shipped stores, that `SetMembershipAsync` stays a whole replace, and that a custom store should
  override the fallback. Verify that the DocFX build in `./scripts/local-gauntlet.sh` passes.
- [ ] 5.2 Add a `CHANGELOG.md` entry under an unreleased 4.7.1 heading that names NextPA finding
  F-033. Verify that it renders in the PR diff.

## 6. Gate

- [ ] 6.1 `./scripts/local-gauntlet.sh` is green, `dotnet test tests/Stratara.Identity.IntegrationTests`
  is green, and `openspec validate change-membership-roles-atomically --strict` passes.

## Workflow follow-up

- Archive with `/opsx:archive` after merge. Per the project convention, the archive rides on the next
  change branch.
- Bump to 4.7.1 and tag only on separate owner approval. Run Sonar on `main` before the tag.
- Mark NextPA finding F-033 in NextPA's `stratara-findings.md` from a NextPA session, not from here.
