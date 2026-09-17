## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.
      *Done:* approved (owner, 2026-09-17).

## 1. The defects, reproduced

- [x] 1.1 Each registration called twice, with the descriptor counts it leaves: `AddStrataraAggregateGrains`,
      `AddStrataraProjectionGrains`, `AddStrataraSagaGrains`, `AddStrataraDurableTimers`,
      `AddStrataraOrleansCommandDispatcher`, `ConfigureStrataraHeavyWork`, `AddStrataraSingletonWork<TWork>`.
      Verify: `tests/Stratara.Orleans.Tests/RegistrationIdempotencyTests.cs` (scenario *A registration is
      called twice*); against today's code the projection, saga and singleton-work cases fail on the doubled
      `INudgeTarget` and `ISingletonWork` — recorded here.
      *Done:* against today's code `AddStrataraProjectionGrains`, `AddStrataraSagaGrains` and `AddStrataraSingletonWork` failed on `ProjectionNudgeTarget x2`, `SagaNudgeTarget x2` and the work `x2`; the other four passed. The shape compares every descriptor by service, key and implementation and leaves out `IConfigureOptions<>`, because the same delegate configured twice is the same value — a separate case asserts that for `ConfigureStrataraHeavyWork`. An eighth case covers the named overload.

- [x] 1.2 A silo that registers the directory directly and one singleton work starts today and is placed
      on as if it hosted everything. Verify: `tests/Stratara.Orleans.IntegrationTests/Hosting/DurableDirectoryCheckTests.cs`
      — a new case expecting the start to fail naming the work and `AddStrataraOrleans`; recorded here that
      it starts against today's code.
      *Done:* against today's code the silo with the directory registered directly and one singleton work started (`Assert.ThrowsAny() Failure: No exception was thrown`). The dispatcher-only case builds its container without `ValidateOnBuild`, because the dispatcher's serializer is the host's and nothing is dispatched; it started before and after.

- [x] 1.3 A singleton work that throws is logged only by the runtime. Verify:
      `tests/Stratara.Orleans.IntegrationTests/Singleton/SingletonWorkTests.cs` — a work that throws on its
      first run, expecting the framework's event and a second run (scenario *A singleton work's run fails*);
      recorded here that no framework event is logged today.
      *Done:* against today's code no `117_119` entry was logged (`Assert.Single() Failure: The collection was empty`).

## 2. Idempotent registrations (D1)

- [x] 2.1 The `INudgeTarget` registrations in `AddStrataraProjectionGrains` and `AddStrataraSagaGrains` are
      guarded by implementation type; `AddStrataraSingletonWork<TWork>` adds `TWork` only when no
      `ISingletonWork` of that type is registered. Verify: `src/Stratara.Orleans/DependencyInjection/OrleansProjectionServiceCollectionExtensions.cs`,
      `OrleansSingletonWorkServiceCollectionExtensions.cs`; 1.1 green for every case.

## 3. The publication check (D2)

- [x] 3.1 `DurableDirectoryCheck.CheckAsync` fails when roles or works are registered and no
      `SingletonWorkSiloMetadata` is, naming the roles' registrations, the works' names and
      `AddStrataraOrleans`; a composition with the directory and neither passes. Verify:
      `src/Stratara.Orleans/Hosting/DurableDirectoryCheck.cs`; `tests/Stratara.Orleans.Tests/DurableDirectoryCheckTests.cs`
      — the message names `AddStrataraProjectionGrains`, the work's name and the call; the dispatcher-only
      composition passes.
      *Done:* deviation from D2: the failure is logged under a new id, `117_120` (`RolesUnpublished`), not `117_107` — two `[LoggerMessage]` methods of one class may not share an id (SYSLIB1006), and the message of `117_107` says no directory is registered, which is not the case. The timer role counts only where an `ITimerOwners` is registered, checked through `IServiceProviderIsService`, as it is published only there. Unit tests in `tests/Stratara.Orleans.Tests/DurableDirectoryCheckTests.cs` (new): role and work named, a work without a name named by its type, dispatcher only passes, timers without an owner check pass, a publishing silo passes.

- [x] 3.2 1.2 green; a second integration case registers the directory directly with the dispatcher only
      and starts (scenario *A silo registers the directory itself and hosts nothing*); `CoHostingTests`
      registers through `AddStrataraOrleans`. Verify: `DurableDirectoryCheckTests`, `CoHostingTests`.

## 4. Visible failures and the registered name (D3, D4)

- [x] 4.1 `LogEvents.Orleans.SingletonWorkFailed` at the next free id in the band (117_114 as of #109) and
      `OrleansLog.LogSingletonWorkFailed(exception, workName)` at error; `SingletonWorkGrain.RunOnceAsync`
      catches, logs and returns. Verify: `src/Stratara.Diagnostics/LogEvents.cs`,
      `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`, `src/Stratara.Orleans/Singleton/SingletonWorkGrain.cs`;
      `DiagnosticsTests` lists the id once; 1.3 green.
      *Done:* `117_119`, the next free id after #116 took `117_115`–`117_118`. A failure to resolve the work by name is a failed run as well and is logged the same way.

- [x] 4.2 `AddStrataraSingletonWork<TWork>(string name, Action<SingletonWorkOptions>? configure = null)` records
      the name in `SingletonWorkRegistrations`; `SingletonWorkSiloMetadata.Fill` writes the registered names
      and constructs only the works registered without one, wrapping a construction failure in a message
      naming the type and why it was constructed; `OutboxDrainWork.WorkName`. Verify:
      `OrleansSingletonWorkServiceCollectionExtensions.cs`, `SingletonWorkPlacement.cs`, `OutboxDrainWork.cs`;
      `SurfaceTests` lists the overload and the constant; `tests/Stratara.Orleans.Tests/SingletonWorkMetadataTests.cs`
      — a named work whose constructor throws is published without being constructed; an unnamed one is
      constructed, and its failure names the type.
      *Done:* `SurfaceTests` lists public types, not members, so neither the overload nor the constant changes it; `llms-full.txt` is regenerated and lists the overload. `SingletonWorkRegistrations` also refuses a work registered under two different names at the second registration, since a work runs under one name; a second registration without a name keeps the first one's.

- [x] 4.3 `SingletonWorkStarter` compares each work's `Name` with its registered name and throws naming both.
      Verify: `SingletonWorkGrain.cs`; `SingletonWorkMetadataTests` — the mismatch case (scenario *A work's
      name differs from the registered one*).
      *Done:* the check is `SingletonWorkRegistrations.EnsureNamed`, called by the starter for every work before any grain is asked to run.

- [x] 4.4 A named work whose constructor records its first construction is constructed after the silo is
      active and runs at its period. Verify: `SingletonWorkTests` (scenario *A work is registered with its
      name*).
      *Done:* `ConstructionProbe` subscribes at `ServiceLifecycleStage.BecomeActive` and every construction of the work records whether that stage had been reached.

## 5. Documentation

- [x] 5.1 `docs/guides/operate-the-orleans-execution-model.md` — *The grain directory*: `AddStrataraOrleans`
      publishes, and a silo that hosts a role or work without it fails naming the call; *What to watch*:
      the new event in the list to route. `docs/guides/migrate-to-the-orleans-execution-model.md` — the
      registrations are idempotent in themselves; the drain registered with its name. Verify: the sections;
      documentation tests.
- [x] 5.2 `docs/reference/di-extensions-cheatsheet.md` (the named overload, the check's second condition),
      `docs/reference/log-events-schema.md` (the row), `src/Stratara.Orleans/README.md` (the named
      registration), `CHANGELOG.md` `[Unreleased]` *Added* (the overload, the constant, the event) and
      *Changed* (idempotent registrations; a silo hosting a role without `AddStrataraOrleans` fails at
      start), `llms.txt`. Verify: the entries; doc-symbol check.
      *Done:* also the examples of the drain in `README.md`, `docs/index.md` and the XML docs of `AddStrataraOrleansCommandDispatcher` and `AddStrataraIntentStore`, and the scenario host's drain, now registered with `OutboxDrainWork.WorkName`.

## 6. Close

- [x] 6.1 `./scripts/local-gauntlet.sh` green; the `Hosting` and `Singleton` integration namespaces green
      against PostgreSQL, Redis and RabbitMQ. Verify: the run output, recorded here.
      *Done:* gauntlet green. `Hosting` and `Singleton`: 21 of 22 on the first run — the new case asserted the work's name where a work registered without one is named by its type, as D2 says; the assertion was corrected and `DurableDirectoryCheckTests` with `CoHostingTests` rerun, 6 of 6. The gauntlet's snippet check needed a `NightlyCleanupWork` placeholder for the unnamed overload's example.

- [x] 6.2 `openspec validate make-the-role-registrations-idempotent-and-their-failures-visible --strict`
      passes. Verify: the output.
      *Done:* valid.

