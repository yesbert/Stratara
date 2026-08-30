# Tasks — A context per operation for the directory stores

Test-first throughout: each implementation task has a test task before it that fails against the
current code for the stated reason.

**The existing store tests are the safety net for the seam, and they must not be edited.** They
exercise the shared-context path through `SqliteDirectoryFixture`. If one of them needs changing to
accommodate the seam, that means the seam changed behaviour on the default path — stop and revise
`design.md` rather than adjusting the test. This is the one rule that makes the churn in group 2 safe.

**Forcing the overlap is part of the test, not an accident of it.** Directory reads against SQLite
complete faster than two calls can be issued, so "two operations at the same time" never actually
overlap and every concurrency test here passes whatever the registration does. The tests use a command
interceptor that holds the first command open until the second has been issued. Discovered while
writing 1.3, which was green for the wrong reason before the gate existed.

## 1. Cover the concurrent case and the default

- [ ] 1.1 In `tests/Stratara.Identity.EntityFrameworkCore.Tests/`, add a test that two overlapping
  membership reads against the factory-backed registration both complete. Fails today: the
  registration does not exist.
- [ ] 1.2 Add the same for the API-key store and for the setting store. Fail today for the same
  reason.
- [x] 1.3 Add a test that the same overlap against the **existing** registration fails with the
  second-operation error, pinning the constraint the documentation now states rather than leaving it
  as folklore. Passes today; it must keep passing, because that path is deliberately unchanged.
- [ ] 1.4 Add a test that the factory-backed setting store still gets its encrypting wrapper when the
  catalog declares an encrypted setting — the settings registration composes rather than plainly
  registering, and that composition has to survive. Fails today: no factory registration exists.
- [ ] 1.5 Add a test that registering both the shared and the factory variant leaves the first one
  registered, pinning the first-wins behaviour named in `design.md`. Fails today: only one exists.
- [ ] 1.6 Run `dotnet test tests/Stratara.Identity.EntityFrameworkCore.Tests` and confirm 1.1, 1.2,
  1.4 and 1.5 fail because the registration is missing, and 1.3 passes.

## 2. Introduce the context seam

- [ ] 2.1 Add an internal seam in `src/Stratara.Identity.EntityFrameworkCore/` that yields a context
  for one operation and releases it, carrying ownership explicitly: a borrowing implementation that
  returns the injected scoped context and disposes nothing, and an owning implementation that creates
  from `IDbContextFactory<TContext>` and disposes.
- [ ] 2.2 Rewrite `EfTenantMembershipStore<TContext>` to acquire its context per operation from the
  seam. Mechanical, one acquisition per public method.
- [ ] 2.3 The same for `EfApiKeyStore<TContext>` — note it commits four times, so check each commit
  still runs against the context that operation acquired.
- [ ] 2.4 The same for `EfSettingStore<TContext>`.
- [ ] 2.5 `dotnet test tests/Stratara.Identity.EntityFrameworkCore.Tests` green **with the existing
  tests unedited**. Confirm by diffing: no existing test file may appear in the diff except for the
  additions from group 1.

## 3. Add the registrations

- [ ] 3.1 In `IdentityDirectoryServiceCollectionExtensions`, add
  `AddTenantMembershipStoreFromContextFactory<TContext>()` registering the store over the owning seam,
  using `TryAddScoped` like its neighbour.
- [ ] 3.2 The same for `AddApiKeyStoreFromContextFactory<TContext>()`.
- [ ] 3.3 The same for `AddSettingStoreFromContextFactory<TContext>()`, preserving the encrypting
  decorator composition and the `ISettingProvider` registration the existing method performs.
- [ ] 3.4 Wire the existing three registrations to the borrowing seam so both paths run the same store
  code.
- [ ] 3.5 `dotnet test tests/Stratara.Identity.EntityFrameworkCore.Tests` green.

## 4. Say what each choice costs

- [ ] 4.1 On each existing registration's XML docs: the stores share the request's context, so
  directory work issued concurrently within one request fails on the second operation, and a store's
  commit also commits whatever the consumer has left unsaved on that context.
- [ ] 4.2 On each new registration's XML docs: a context per operation, so concurrent directory work
  is safe and a store's commit touches only its own rows — and in exchange a store write does not join
  a transaction the consumer opened on their own context. State that
  `AddDbContextFactory<TContext>()` must be registered, and that a consumer should keep the factory's
  configuration aligned with their scoped registration.
- [ ] 4.3 On both: calling both registrations leaves the first one registered. Say it on both, since a
  consumer making that mistake is reading one of them.
- [ ] 4.4 Confirm no XML doc added here mentions the internal seam type — it is an implementation
  detail and these docs ship in the package.

## 5. Close out

- [x] 5.1 `openspec validate offer-identity-stores-a-context-per-operation --strict` clean.
- [x] 5.2 `./scripts/local-gauntlet.sh` green.
- [x] 5.3 CHANGELOG entry under the next version: the new registrations, what each choice costs, and
  that nothing changes for a consumer who does not adopt them.
