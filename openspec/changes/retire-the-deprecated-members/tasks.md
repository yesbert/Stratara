# Tasks — Retire the deprecated members that were held for the major

Six removals in three packages, grouped so each verifies on its own. Groups 2–4 are independent of
each other and can be done in any order; group 1 is the ground they all stand on, and groups 5–7
close the change.

## 1. Confirm the ground before removing

- [ ] 1.1 Confirm the six `[Obsolete]` sites are still exactly the six named in `proposal.md` —
      `ISnapshotRepository.cs:52,62`, `SnapshotRepository.cs:50,58`,
      `NpgsqlDbContextServiceCollectionExtensions.cs:49`,
      `AuthorizationExceptionApplicationBuilderExtensions.cs:28`. Verify:
      `grep -rn "Obsolete(" src --include="*.cs"` returns those six lines and nothing else. A seventh
      means somebody deprecated something since the proposal was written — decide whether it belongs
      in `4.0.0` before continuing.
- [ ] 1.2 Confirm no framework code calls a type-less snapshot overload. Verify: every call to
      `GetAsync` and `GetLatestVersionOrDefaultAsync` in `src/` passes an `aggregateTypeName` —
      `AggregationService.cs:44` and `SnapshotService.cs:70` are the only two call sites.
- [ ] 1.3 Confirm `AuthorizationExceptionMiddleware` is registered nowhere but the extension being
      removed. Verify: `grep -rn "AuthorizationExceptionMiddleware" src` returns only its own file
      and `AuthorizationExceptionApplicationBuilderExtensions.cs`. If a composite registers it, the
      decision in `design.md` to delete it does not hold and this stops here.

## 2. Remove the type-less snapshot lookups (2 findings)

- [ ] 2.1 Delete both obsolete members from
      `src/Stratara.Abstractions/Abstractions/EventSourcing/ISnapshotRepository.cs` — the
      `GetAsync(Guid, long?, CancellationToken)` and
      `GetLatestVersionOrDefaultAsync(Guid, CancellationToken)` declarations with their XML
      documentation and `<remarks>`. Verify: the interface has exactly three members left —
      `GetAsync` with `aggregateTypeName`, `AddAsync`, and `GetLatestVersionOrDefaultAsync` with
      `aggregateTypeName` — and `Stratara.Abstractions` builds with no `CS1591`.
- [ ] 2.2 Delete their implementations from
      `src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/SnapshotRepository.cs`.
      Verify: the class no longer carries an `[Obsolete]` attribute and
      `dotnet build src/Stratara.EventSourcing.EntityFrameworkCore` is clean — an interface member
      left unimplemented would fail here, which is the check that 2.1 and 2.2 agree.
- [ ] 2.3 Check the `<see cref="..."/>` in `src/Stratara.Abstractions/EventSourcing/Snapshot.cs:26`
      still resolves. It points at the type-scoped `GetAsync` overload, which survives, so this is a
      confirmation rather than an edit. Verify: the build produces no `CS1574` (unresolved cref) —
      `TreatWarningsAsErrors` makes a dangling cref a build failure, so a clean build is the proof.
- [ ] 2.4 Verify nothing downstream broke: `dotnet test tests/Stratara.Infrastructure.Tests` and
      `dotnet test tests/Stratara.EntityFrameworkCore.Tests` green. `SnapshotServiceTests` and
      `AggregationServiceTests` mock only the type-scoped signatures, so they should need no edit —
      if either needs one, something other than a deprecated member was removed.

## 3. Remove the misspelled registration alias (1 finding)

- [ ] 3.1 Delete `AddNpsqlWriteDbContextFactory<TDbContext>` from
      `src/Stratara.EventSourcing.EntityFrameworkCore/EntityFrameworkCore/DependencyInjection/NpgsqlDbContextServiceCollectionExtensions.cs:49`,
      with its XML documentation. Verify: `AddNpgsqlWriteDbContextFactory` — with the `g` — is
      untouched, keeps its `<example>`, and `grep -rn "AddNpsql" src tests samples` returns nothing.
- [ ] 3.2 Rewrite the rename note in
      `src/Stratara.EventSourcing.EntityFrameworkCore/README.md:54-56`. It currently promises the
      removal that just happened. State it in the past: the write-side factory was
      `AddNpsqlWriteDbContextFactory` through `3.1.x`, was renamed in `3.2.0`, and the old spelling
      was removed in `4.0.0`. Verify: the note names both spellings and the two versions, so a
      consumer arriving from `3.1.x` still finds the answer.
- [ ] 3.3 `dotnet test tests/Stratara.EntityFrameworkCore.Tests` green, and the documentation suite
      with it — `dotnet test tests/Stratara.Documentation.Tests`, which compiles every shipped
      example and enumerates the registration surface.

## 4. Remove the bare-403 middleware and its registration (1 finding)

- [ ] 4.1 Delete
      `src/Stratara.Infrastructure/DependencyInjection/AuthorizationExceptionApplicationBuilderExtensions.cs`
      entirely — `UseAuthorizationExceptionTo403` is its only member, so the file goes with it.
- [ ] 4.2 Delete `src/Stratara.Infrastructure/Middlewares/AuthorizationExceptionMiddleware.cs`, per
      the decision in `design.md`. Verify: `dotnet build src/Stratara.Infrastructure` is clean, which
      proves nothing else referenced it.
- [ ] 4.3 Delete
      `tests/Stratara.Infrastructure.Tests/DependencyInjection/AuthorizationExceptionApplicationBuilderExtensionsTests.cs`
      and `tests/Stratara.Infrastructure.Tests/Middlewares/AuthorizationExceptionMiddlewareTests.cs`.
      Verify: before deleting, confirm each surviving behaviour has a home — the problem-details tests
      in `tests/Stratara.ServiceDefaults.AspNetCore.Tests` (or wherever
      `AddStrataraProblemDetails` is covered) assert the 403 for an authorization refusal and a
      tenant-access denial, and assert that an unrelated exception propagates. If any of those three
      is not covered there, add it there rather than keeping the old test file.
- [ ] 4.4 Remove the now-impossible double-registration warning from the XML documentation of
      `src/Stratara.ServiceDefaults.AspNetCore/StrataraProblemDetailsServiceCollectionExtensions.cs:25,35`.
      Both passages define `AddStrataraProblemDetails` by contrast with a member that no longer
      exists. Verify: the `<summary>` still says what the registration does on its own terms, the
      `<example>` survives (`RegistrationDocumentationTests` requires one), and no `<see cref>` points
      at a deleted type.
- [ ] 4.5 `dotnet test tests/Stratara.Infrastructure.Tests` green, and the problem-details suite green.

## 5. Bring the specification and the derived documentation into line

- [ ] 5.1 The `authorization` delta in `specs/authorization/spec.md` replaces the requirement
      *"A denial reaches an HTTP caller as 403"*. Verify: `openspec validate retire-the-deprecated-members --strict`
      passes, and the modified requirement's header text matches
      `openspec/specs/authorization/spec.md` exactly — a mismatch loses the requirement at archive
      time.
- [ ] 5.2 Rewrite `docs/guides/write-a-validator.md:116`. The paragraph introduces
      `AddStrataraProblemDetails()` by what it supersedes and warns against registering both. Both
      halves are now false. Verify: the section still tells a reader what a validation rejection, an
      authorization refusal and a tenant-access denial each become, and names no removed member.
- [ ] 5.3 Rewrite `docs/guides/enforce-tenant-isolation.md:99`, which names
      `AuthorizationExceptionMiddleware` as what maps `TenantAccessDeniedException` to 403. Verify:
      it names `AddStrataraProblemDetails()` instead and says the mapping is opt-in, matching the
      amended requirement.
- [ ] 5.4 Rewrite the same sentence in `src/Stratara.Mediator/README.md:75`. Verify: the README ships
      inside the package, so re-read it for the `<see cref>`-free plain-text form the other README
      passages use.
- [ ] 5.5 Confirm no derived page still names a removed member. Verify:
      `grep -rn "UseAuthorizationExceptionTo403\|AuthorizationExceptionMiddleware\|AddNpsql" docs src --include="*.md"`
      returns nothing outside `docs/_site/` (generated output, regenerated by `docs.yml`) and
      `CHANGELOG.md` (a historical record, which is supposed to keep naming them).

## 6. Record what a consumer has to do

- [ ] 6.1 CHANGELOG entry under `[Unreleased]` as **BREAKING**, with a migration table: each removed
      member, its successor, and the before/after call. Verify: all six rows are present, and the
      before/after for the snapshot overloads shows where the `aggregateTypeName` argument comes from
      (`aggregateType.GetQualifiedTypeName()`).
- [ ] 6.2 Give the 403 removal its own paragraph rather than a table row, per `design.md`: it is the
      one removal whose successor is not signature-for-signature equivalent — the response gains an
      RFC 7807 body, and a host that wants neither must catch `AuthorizationException` and
      `TenantAccessDeniedException` itself, both of which are declared in `Stratara.Abstractions`.
      Verify: a reader can tell from the entry alone whether their host is affected.
- [ ] 6.3 Note the `ISnapshotRepository` implementor case: removing an interface member leaves their
      two implementations as dead code rather than breaking their build. Verify: the entry says so,
      so nobody hunts for a compile error that will not appear.

## 7. Close it out

- [ ] 7.1 `./scripts/local-gauntlet.sh` green, including the documentation tests that compile every
      shipped example.
- [ ] 7.2 Confirm the surface is genuinely free of deprecations. Verify:
      `grep -rn "Obsolete(" src --include="*.cs"` returns nothing at all.
- [ ] 7.3 Open the pull request through the `/pr` skill, let `Build + unit tests` pass, merge.
      Verify: the required check is green on the pull request, not only locally.
- [ ] 7.4 Run the analysis workflow by hand on `main` and read the gate. Verify: `new_violations` is
      **0**. Six means this change did not land; anything between one and five means one of the
      removals was partial; anything else means `leave-no-analysis-issue-open` has not shipped yet or
      something new arrived — read the report before assuming which.
- [ ] 7.5 Update the project's state file: the quality gate now stands at zero, and the deprecation
      reminders it was carrying are collected. Verify: a reader who sees a red nightly after this can
      tell within one file that red is no longer expected.
