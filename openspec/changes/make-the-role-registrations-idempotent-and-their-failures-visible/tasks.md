## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. The defects, reproduced

- [ ] 1.1 Each registration called twice, with the descriptor counts it leaves: `AddStrataraAggregateGrains`,
      `AddStrataraProjectionGrains`, `AddStrataraSagaGrains`, `AddStrataraDurableTimers`,
      `AddStrataraOrleansCommandDispatcher`, `ConfigureStrataraHeavyWork`, `AddStrataraSingletonWork<TWork>`.
      Verify: `tests/Stratara.Orleans.Tests/RegistrationIdempotencyTests.cs` (scenario *A registration is
      called twice*); against today's code the projection, saga and singleton-work cases fail on the doubled
      `INudgeTarget` and `ISingletonWork` — recorded here.
- [ ] 1.2 A silo that registers the directory directly and one singleton work starts today and is placed
      on as if it hosted everything. Verify: `tests/Stratara.Orleans.IntegrationTests/Hosting/DurableDirectoryCheckTests.cs`
      — a new case expecting the start to fail naming the work and `AddStrataraOrleans`; recorded here that
      it starts against today's code.
- [ ] 1.3 A singleton work that throws is logged only by the runtime. Verify:
      `tests/Stratara.Orleans.IntegrationTests/Singleton/SingletonWorkTests.cs` — a work that throws on its
      first run, expecting the framework's event and a second run (scenario *A singleton work's run fails*);
      recorded here that no framework event is logged today.

## 2. Idempotent registrations (D1)

- [ ] 2.1 The `INudgeTarget` registrations in `AddStrataraProjectionGrains` and `AddStrataraSagaGrains` are
      guarded by implementation type; `AddStrataraSingletonWork<TWork>` adds `TWork` only when no
      `ISingletonWork` of that type is registered. Verify: `src/Stratara.Orleans/DependencyInjection/OrleansProjectionServiceCollectionExtensions.cs`,
      `OrleansSingletonWorkServiceCollectionExtensions.cs`; 1.1 green for every case.

## 3. The publication check (D2)

- [ ] 3.1 `DurableDirectoryCheck.CheckAsync` fails when roles or works are registered and no
      `SingletonWorkSiloMetadata` is, naming the roles' registrations, the works' names and
      `AddStrataraOrleans`; a composition with the directory and neither passes. Verify:
      `src/Stratara.Orleans/Hosting/DurableDirectoryCheck.cs`; `tests/Stratara.Orleans.Tests/DurableDirectoryCheckTests.cs`
      — the message names `AddStrataraProjectionGrains`, the work's name and the call; the dispatcher-only
      composition passes.
- [ ] 3.2 1.2 green; a second integration case registers the directory directly with the dispatcher only
      and starts (scenario *A silo registers the directory itself and hosts nothing*); `CoHostingTests`
      registers through `AddStrataraOrleans`. Verify: `DurableDirectoryCheckTests`, `CoHostingTests`.

## 4. Visible failures and the registered name (D3, D4)

- [ ] 4.1 `LogEvents.Orleans.SingletonWorkFailed` at the next free id in the band (117_114 as of #109) and
      `OrleansLog.LogSingletonWorkFailed(exception, workName)` at error; `SingletonWorkGrain.RunOnceAsync`
      catches, logs and returns. Verify: `src/Stratara.Diagnostics/LogEvents.cs`,
      `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`, `src/Stratara.Orleans/Singleton/SingletonWorkGrain.cs`;
      `DiagnosticsTests` lists the id once; 1.3 green.
- [ ] 4.2 `AddStrataraSingletonWork<TWork>(string name, Action<SingletonWorkOptions>? configure = null)` records
      the name in `SingletonWorkRegistrations`; `SingletonWorkSiloMetadata.Fill` writes the registered names
      and constructs only the works registered without one, wrapping a construction failure in a message
      naming the type and why it was constructed; `OutboxDrainWork.WorkName`. Verify:
      `OrleansSingletonWorkServiceCollectionExtensions.cs`, `SingletonWorkPlacement.cs`, `OutboxDrainWork.cs`;
      `SurfaceTests` lists the overload and the constant; `tests/Stratara.Orleans.Tests/SingletonWorkMetadataTests.cs`
      — a named work whose constructor throws is published without being constructed; an unnamed one is
      constructed, and its failure names the type.
- [ ] 4.3 `SingletonWorkStarter` compares each work's `Name` with its registered name and throws naming both.
      Verify: `SingletonWorkGrain.cs`; `SingletonWorkMetadataTests` — the mismatch case (scenario *A work's
      name differs from the registered one*).
- [ ] 4.4 A named work whose constructor records its first construction is constructed after the silo is
      active and runs at its period. Verify: `SingletonWorkTests` (scenario *A work is registered with its
      name*).

## 5. Documentation

- [ ] 5.1 `docs/guides/operate-the-orleans-execution-model.md` — *The grain directory*: `AddStrataraOrleans`
      publishes, and a silo that hosts a role or work without it fails naming the call; *What to watch*:
      the new event in the list to route. `docs/guides/migrate-to-the-orleans-execution-model.md` — the
      registrations are idempotent in themselves; the drain registered with its name. Verify: the sections;
      documentation tests.
- [ ] 5.2 `docs/reference/di-extensions-cheatsheet.md` (the named overload, the check's second condition),
      `docs/reference/log-events-schema.md` (the row), `src/Stratara.Orleans/README.md` (the named
      registration), `CHANGELOG.md` `[Unreleased]` *Added* (the overload, the constant, the event) and
      *Changed* (idempotent registrations; a silo hosting a role without `AddStrataraOrleans` fails at
      start), `llms.txt`. Verify: the entries; doc-symbol check.

## 6. Close

- [ ] 6.1 `./scripts/local-gauntlet.sh` green; the `Hosting` and `Singleton` integration namespaces green
      against PostgreSQL, Redis and RabbitMQ. Verify: the run output, recorded here.
- [ ] 6.2 `openspec validate make-the-role-registrations-idempotent-and-their-failures-visible --strict`
      passes. Verify: the output.
