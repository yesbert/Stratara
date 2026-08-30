## 1. Confirm the ground before editing

- [x] 1.1 Establish that the analyser's `multicriteria` suppression list is inert: `CA1859` is in it
      with a resource key of `**`, and `EfApiKeyStore.cs:348` reports it anyway. Verify: the finding
      is present in the report artifact of the most recent analysis run. If the list turns out to
      work after all, say so — decision 2 of `design.md` rests on it not working.
- [x] 1.2 Keep the report of the last run to hand as the worklist. Verify: it lists 57 findings, 6 of
      them `S1133`, and the counts per rule below add up to the remaining 51.

## 2. Use what `Assert.Single` returns (30 findings, 16 files)

Each is the same edit: `Assert.Single(x)` hands back the element, so take it instead of indexing
`x[0]` for it afterwards. Run the project's tests after each file; a substitution that drops the
assertion is the failure mode to watch for.

- [x] 2.1 `tests/Stratara.Infrastructure.Tests/EventSourcing/EventSourceTests.cs` (8) and
      `.../EventSourcing/EventStreamTests.cs` (1). Verify: `dotnet test tests/Stratara.Infrastructure.Tests` green.
- [x] 2.2 `tests/Stratara.Shared.Tests/` — `EventMapperFactoryTests.cs` (4),
      `Merging/ChangeSetBuilderTests.cs` (2), `EventBundleMapperTests.cs` (1),
      `Merging/ChangeMergerTests.cs` (1), `EventSourcing/EventStreamTests.cs` (1). Verify:
      `dotnet test tests/Stratara.Shared.Tests` green.
- [x] 2.3 `tests/Stratara.Projections.Tests/` — `Services/ProjectionWorkerMetricsTests.cs` (2),
      `ProjectionMethodInvokerTests.cs` (1), `Services/ProjectionHandlerTests.cs` (1). Verify:
      `dotnet test tests/Stratara.Projections.Tests` green.
- [x] 2.4 `tests/Stratara.Sagas.Tests/Services/` — `SagaWorkerMetricsTests.cs` (2),
      `SagaMethodInvokerTests.cs` (1). Verify: `dotnet test tests/Stratara.Sagas.Tests` green.
- [x] 2.5 The remaining four, one each:
      `tests/Stratara.Outbox.RabbitMQ.Tests/Mediator/MediatorCommandWorkerTests.cs` (2),
      `tests/Stratara.EntityFrameworkCore.Tests/IdentityStore/IdentityStoreTests.cs`,
      `tests/Stratara.Validation.Tests/ValidationPipelineBehaviorTests.cs`,
      `tests/Stratara.Testing.Tests/InMemoryMessageBusTests.cs`. Verify: each project's tests green.

## 3. Say what the quiet tests assert (3 findings)

- [x] 3.1 In `tests/Stratara.Infrastructure.Tests/EventSourcing/AggregateSnapshotShapeGuardTests.cs`,
      lines 60, 77 and 92: replace the bare `await GuardOver(typeof(X)).StartAsync(...)` with
      `Assert.Null(await Record.ExceptionAsync(() => GuardOver(typeof(X)).StartAsync(...)))`. Verify:
      the three tests still pass, and each fails when the guard is made to throw for its input —
      check at least one by temporarily breaking it.

## 4. Compile the regular expressions (4 findings, 3 files)

- [x] 4.1 `tests/Stratara.Documentation.Tests/AiIndexTests.cs:15,18`,
      `tests/Stratara.Documentation.Tests/LogEventAllocationTests.cs`,
      `tests/Stratara.Shared.Tests/Reflections/TrustedTypeResolverTests.cs`: move each to
      `[GeneratedRegex]`, which needs a `partial` method on a `partial` type. Verify: both projects
      build without `SYSLIB1045` and their tests pass. If a conversion turns contorted, leave that
      one and record why in the task rather than forcing it.

## 5. The small source-side fixes (8 findings)

- [x] 5.1 Overload adjacency (`S4136`): move the `GetLatestVersionOrDefaultAsync` overloads next to
      each other in `src/Stratara.Abstractions/Abstractions/EventSourcing/ISnapshotRepository.cs` and
      `src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/SnapshotRepository.cs`.
      Verify: no member is renamed or removed — this is ordering only, so the public surface is
      byte-identical in shape.
- [x] 5.2 Loops to `Where` (`S3267`): `src/Stratara.Mediator/Authorization/AuthorizingMediator.cs`,
      `src/Stratara.Infrastructure/Authorization/AuthorizingCommandOutboxDispatcher.cs`,
      `src/Stratara.Abstractions/Authorization/PermissionCatalog.cs`. Verify: the authorization tests
      pass — these three sit on the deny path, where a rewritten filter is worth reading twice.
- [x] 5.3 Parameter name (`S927`): rename `builder` to `modelBuilder` in
      `src/Stratara.Identity.EntityFrameworkCore/IdentityDirectoryDbContext.cs:40` to match the
      overridden declaration. Verify: `dotnet test tests/Stratara.Identity.EntityFrameworkCore.Tests`
      green.
- [x] 5.4 `Assert.IsAssignableFrom` (`xUnit2032`) in
      `tests/Stratara.Infrastructure.Tests/Authorization/AuthorizingMediatorPermissionTests.cs` and
      `tests/Stratara.Shared.Tests/Domain/TenantAggregateTests.cs`: use the `Assert.IsType` overload
      that allows a derived type. Verify: both tests still distinguish the type they are about.

## 6. The three that stay, with their reason at the site

- [x] 6.1 `S2068` ×2 in `src/Stratara.Outbox.RabbitMQ/Messaging/RabbitMqBus.cs:261,267`. Both are
      text: one inside the exception thrown when credentials are missing outside Development, one in
      the sentence telling an operator to set `RABBITMQ_USERNAME=guest` deliberately. Add
      `[SuppressMessage]` with a justification naming that. Verify: re-read both lines first and
      confirm no literal credential is assigned to anything.
- [x] 6.2 `S3011` in `src/Stratara.Infrastructure/EventSourcing/AggregateSnapshotShapeGuard.cs:78`.
      The reflection reads the compiler-generated backing field to tell an auto-property from a
      computed one — there is no other way to make that distinction, and the method's own
      documentation already explains why it matters. Suppress with that as the justification.
      Verify: the justification says what the bypass is for, not merely that it is intended.
- [x] 6.3 `CA1859` in `src/Stratara.Identity.EntityFrameworkCore/EfApiKeyStore.cs:348` —
      **look before suppressing.** It is a private helper, so the project's rule about
      `IReadOnlyList<T>` on *public* surfaces may not apply. Follow the call site: if the concrete
      type flows in unchanged, change the parameter type and the finding is gone properly. Suppress
      only if that would force a caller to materialise a collection it does not otherwise need, and
      say so in the justification. Verify: whichever way it goes, the reason is written down.

## 7. Close it out

- [x] 7.1 `./scripts/local-gauntlet.sh` green. Verify: it passes, including the documentation tests
      that compile every shipped example.
- [ ] 7.2 Open the pull request, let `Build + unit tests` pass, merge. Verify: the required check is
      green on the pull request, not only locally.
- [ ] 7.3 Run the analysis workflow by hand on `main` and read the gate. Verify: `new_violations` is
      **6**, and all six are `S1133` on the deprecated members named in `proposal.md`. Any other
      number means something in groups 2–6 did not land, or the run picked up something new.
- [ ] 7.4 Record in the project's state file that the gate stands at six deprecation reminders and
      that they clear with `4.0.0`. Verify: a reader who sees a red nightly knows within one file
      whether it is expected.
