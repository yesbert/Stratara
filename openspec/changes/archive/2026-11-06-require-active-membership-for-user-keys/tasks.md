# Tasks

## 1. EF store

- [x] 1.1 Add the failing tests to `tests/Stratara.Identity.EntityFrameworkCore.Tests/EfApiKeyStoreTests.cs` and record the red run: a token fails validation for a former, a pending and a removed membership, and for a membership only in another tenant; verify with `dotnet test tests/Stratara.Identity.EntityFrameworkCore.Tests`
- [x] 1.2 Add the guarding tests to the same class, which pass before and after: an active membership validates, and a machine key validates whatever its membership row says
- [x] 1.3 Add the return test to the same class: a token that failed validates again when the membership is active again, its descriptor is unchanged and `RevokedAt` is still null; a token revoked or expired meanwhile stays refused
- [x] 1.4 Implement the check in `ValidateAsync` of `src/Stratara.Identity.EntityFrameworkCore/EfApiKeyStore.cs` as one statement; verify that the tests of 1.1 to 1.3 pass and that `DirectoryContextSharingTests` still passes for both context sources
- [x] 1.5 Update the XML documentation of `ValidateAsync` and the fail-closed remark in `src/Stratara.Abstractions/Abstractions/ApiKeys/IApiKeyStore.cs`, stating that a custom store must make the same check; verify that `dotnet build` of the project passes with CS1591 as an error

## 2. In-memory store

- [x] 2.1 Add the failing tests to `tests/Stratara.Testing.Tests/InMemoryApiKeyStoreTests.cs` for former, pending and removed; guarding tests for active, return to active and a machine key; verify with `dotnet test tests/Stratara.Testing.Tests`
- [x] 2.2 Implement the check in `src/Stratara.Testing/InMemoryApiKeyStore.cs` through its `Memberships` store, and update its summary and `src/Stratara.Testing/README.md`; verify that the tests of 2.1 pass
- [x] 2.3 Run the other suites that use the double, `tests/Stratara.Infrastructure.Tests` and `tests/Stratara.WriteStore.Tests`, one at a time; verify that their counts are unchanged and green

## 3. HTTP surface and shared store

- [x] 3.1 Add one test in `tests/Stratara.Identity.AspNetCore.Tests` showing the scheme fails with no principal when the store returns no descriptor for a user-bound key, proved by `ApiKeyAuthenticationHandlerTests.Personal_access_token_without_an_active_membership_yields_no_principal`, which runs the scheme over the real in-memory store. Update the remark in `src/Stratara.Identity.AspNetCore/Authentication/ApiKeyAuthenticationHandler.cs`
- [x] 3.2 Add a test to `tests/Stratara.Identity.IntegrationTests/SharedIdentityStoreTests.cs` on the shared identity store: a token validates while active, fails when former, validates on return; verify with `--filter-class` on that class alone, after checking `uptime`

## 4. Documentation

- [x] 4.1 Update `docs/guides/api-keys-and-pats.md`: the condition, the 401, no revocation and the return, the obligation of a custom `IApiKeyStore`, a machine key for an identity with no person's membership, and the invariants table; verify against the delta in `specs/api-keys/spec.md`
- [x] 4.2 Add the clarifying clause to `docs/guides/tenant-membership.md` where it says an API key keeps the tenant it carries; verify that it agrees with the `tenant-directory` section of `design.md`
- [x] 4.3 Verify `openspec validate require-active-membership-for-user-keys --strict` passes

## Workflow follow-up

- The owner reviews the uncommitted diff; committing, pushing and the CHANGELOG entry are outside this task list.
- Archive the change after that review. The changelog entry should name the obligation on a custom `IApiKeyStore`.
