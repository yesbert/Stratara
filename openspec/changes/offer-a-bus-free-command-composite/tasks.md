## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. The composite (D1)

- [ ] 1.1 Failing test first: `WorkerDefaultsCompositesTests.AddCommandServices_RegistersTheCommandStackWithoutTheWorker`
      asserts `IMediator`, `ICommandOutboxDispatcher`, `IEventBundleOutboxDispatcher`, `IMessageBus` and no
      `IHostedService` implemented by `MediatorCommandWorker`; it does not compile against today's code.
      Verify: `tests/Stratara.EventSourcing.WorkerDefaults.Tests/WorkerDefaultsCompositesTests.cs`.
- [ ] 1.2 `AddCommandServices` in `WorkerDefaultsHostBuilderExtensions`, and `AddCommandWorkerServices`
      rewritten as `AddCommandServices` + `AddMediatorWorker`; XML docs in the house style with an example.
      Verify: `src/Stratara.EventSourcing.WorkerDefaults/WorkerDefaultsHostBuilderExtensions.cs`; 1.1 green;
      `AddCommandWorkerServices_RegistersMediatorWorkerHostedService` still green.
- [ ] 1.3 The package README's table and the csproj description name the composite. Verify:
      `src/Stratara.EventSourcing.WorkerDefaults/README.md`, `Stratara.EventSourcing.WorkerDefaults.csproj`.

## 2. The broker-free guarantee (D2)

- [ ] 2.1 Integration test *A command silo runs without a broker*: a silo composed as the guide's command
      row will read (`AddCommandServices`, `AddStrataraOrleansCommandDispatcher`, `AddStrataraIntentStore`,
      `AddStrataraAggregateGrains`, `AddEventProjectionServices`, `AddStrataraProjectionGrains`) and a
      client host with `AddBackendServices` + the dispatcher, no `rabbitmq` connection string, the
      `IMessageBus` singleton replaced by a double that throws on every member; dispatch from the client,
      assert the handler ran on the silo, the projection applied, the outbox holds no bundle. Verify:
      `tests/Stratara.Orleans.IntegrationTests/Hosting/BrokerFreeSiloTests.cs`; run once with
      `AddCommandWorkerServices` in place of the composite and record that the start fails on the worker.
- [ ] 2.2 Scenario *A silo commits without a store-reading role*: the same test class composes a silo with
      the command role only and a recording bus double, commits, and asserts the bundle was published.
      Verify: the second test in `BrokerFreeSiloTests.cs`.

## 3. Documentation (D3, D4)

- [ ] 3.1 `docs/guides/migrate-to-the-orleans-execution-model.md`: the command row of *Adopt the roles*
      (composite, dispatcher, intent store, aggregate grains; what changes), the subsection *When the
      broker can go*, and the section *After the cut-over: the bus queues* replacing the last clause of
      step 6 — queues by subscription, the five-step order, the cost of a publisher left behind, the
      external-consumer case. Verify: the sections; documentation tests (the snippets compile).
- [ ] 3.2 `docs/reference/di-extensions-cheatsheet.md` and `docs/getting-started/di-composition.md` gain the
      composite's row; `src/Stratara.Orleans/README.md` quick start shows a command silo with it. Verify:
      the rows; doc-symbol check.
- [ ] 3.3 `CHANGELOG.md` `[Unreleased]` *Added* (the composite, the guarantee) and *Changed* (the guide's
      command row and queue procedure); `llms.txt` and `llms-full.txt` regenerated. Verify: the entries.

## 4. Close

- [ ] 4.1 `./scripts/local-gauntlet.sh` green; the `Hosting` integration namespace green against PostgreSQL
      and Redis, with and without the RabbitMQ container. Verify: the run output, recorded here.
- [ ] 4.2 `openspec validate offer-a-bus-free-command-composite --strict` passes. Verify: the output.
