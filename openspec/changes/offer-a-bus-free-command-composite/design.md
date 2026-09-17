## Context

See `proposal.md` — Why. The current code:

- **Composites.** `AddCommandWorkerServices` (`src/Stratara.EventSourcing.WorkerDefaults/WorkerDefaultsHostBuilderExtensions.cs:60-70`)
  is `AddCommonFrameworkServices` + `AddMediator` + `AddMediatorWorker` + `AddWriteStore` +
  `AddEventSourcing` + `AddOutboxDispatcher`. `AddEventProjectionServices` (`:142-151`) and
  `AddSagaServices` (`:193-202`) are the projection and saga stacks without `AddProjectionWorker` /
  `AddSagaWorker`; their worker composites call them and add the worker (`:126-130`, `:174-178`). No
  command equivalent exists.
- **The bus.** `AddCommonFrameworkServices` (`:276-286`) calls `AddMessaging`
  (`src/Stratara.Outbox.RabbitMQ/DependencyInjection/MessagingServiceCollectionExtensions.cs:36-50`),
  which registers `RabbitMqBus` as the singleton `IMessageBus`, binds `MessagingOptions` and
  `BusEnvelopeJsonOptions` without validation, and binds `MessageRetryOptions` with `ValidateOnStart`.
  `RabbitMqBus` opens its connection on first use (`RabbitMqBus.cs:150`), not in its constructor. Its
  consumers are the two bus dispatchers (`CommandOutboxDispatcher`, `EventBundleOutboxDispatcher`), the
  three bus workers and the bus itself.
- **What the execution model replaces.** `AddStrataraOrleansCommandDispatcher` takes the undecorated
  `ICommandOutboxDispatcher` slot (`OrleansAggregateServiceCollectionExtensions.cs:84-131`).
  `AddStrataraProjectionGrains` / `AddStrataraSagaGrains` replace the `IEventBundleOutboxDispatcher`
  with `OrleansEventBundleDispatcher` unless `hybrid` (`OrleansProjectionServiceCollectionExtensions.cs:141-170`),
  which nudges the nudge targets registered on the same silo (`OrleansEventBundleDispatcher.cs:36-59`);
  the targets are the projection names and the saga consumer registered there
  (`ProjectionGrain.cs:216-239`). `AddStrataraAggregateGrains` replaces nothing (`:32-44`).
  `OutboxDrainWork` skips the bus command pass when an intent store is registered and the bundle pass
  when the dispatcher is `OrleansEventBundleDispatcher { StoresBundles: false }` (`OutboxDrainWork.cs:32-49`).
- **The guide.** The command row (`docs/guides/migrate-to-the-orleans-execution-model.md:181`) names
  `AddCommandWorkerServices` and `AddStrataraAggregateGrains()` only; the dispatcher and the intent
  store appear in the API row (`:180`). Step 6 of *Upgrade in this order* (`:118-120`) ends with
  "delete the queues nothing consumes any more — a quorum queue without a consumer grows". The bus's
  own remarks say a publish to an exchange without a bound queue is returned and stored in the outbox
  table for the drain (`RabbitMqBus.cs:47-52`); the queue names are `<subscription>.v2` with
  `<subscription>.dead-letter` beside each (`:29-40`), the subscriptions those of `IMessagingIdentifier`.
- **The tests.** `WorkerDefaultsCompositesTests` asserts each composite's registrations;
  `CoHostingTests.cs:98` composes the silo with `AddCommandWorkerServices` and a RabbitMQ container;
  every silo of the integration suite has a broker.

## Goals / Non-Goals

**Goals:**
- A command silo on the execution model is composed like a projection or saga silo: the role's
  services without the bus-fed worker, then the execution model's registrations.
- A host whose two dispatchers the execution model replaced is proven to need no broker, and the
  guide says under which composition that holds.
- The guide says what to do with the queues, in the order that loses nothing.

**Non-Goals:**
- Removing the bus from the common base. `AddMessaging` binds `MessageRetryOptions`, which the drain
  and the resume bound read, and registers a bus that costs nothing until resolved; a composite
  without it would change what every composite is specified to register.
- A bundle dispatcher for a command silo that carries no store-reading role. Such a silo has no
  projection names to wake; a wake-up dispatcher there would wake nothing and the readers' poll would
  be the only bound. The guide states the limit instead: a silo that commits registers a store-reading
  role or keeps the bus.
- A package split that frees `Stratara.EventSourcing.WorkerDefaults` from `Stratara.Outbox.RabbitMQ`.
  The package dependency stays; the runtime dependency is what this change removes.
- Deleting queues from code. The queues belong to the operator; the framework documents the order.

## Decisions

### D1 — `AddCommandServices` is the command worker composite minus the worker, nothing else

`AddCommandServices()` in `WorkerDefaultsHostBuilderExtensions`: `AddCommonFrameworkServices` +
`AddMediator` + `AddWriteStore` + `AddEventSourcing` + `AddOutboxDispatcher`. `AddCommandWorkerServices`
becomes `AddCommandServices` plus `AddMediatorWorker`, the way the projection and saga worker
composites are built from their services composites, so the two cannot drift. The name follows
`AddEventProjectionServices` / `AddSagaServices`: the role's services, without the worker. The bus
dispatchers stay registered, as they do after the other two services composites, so that
`AddStrataraOrleansCommandDispatcher` finds the slot it replaces and a host that has not adopted the
execution model yet still dispatches its handlers' sends to the bus.

*Rejected: a composite that also drops the bus dispatchers.* The composite cannot register the
execution model's dispatcher (it needs an intent store the composite does not know) and a write path
without any `IEventBundleOutboxDispatcher` fails at the first commit; a no-op bundle dispatcher would
silently drop bundles for a host whose projections still read the bus.
*Rejected: `hybrid`-style flags on `AddStrataraAggregateGrains` that replace the bundle dispatcher.*
A command silo without a store-reading role has nothing to wake (Non-Goals), and a default that stops
publishing would silently starve bus projection workers of a deployment mid-rollout.

Evidence: `WorkerDefaultsHostBuilderExtensions.cs:60-70,126-151,174-202`;
`WorkerDefaultsCompositesTests.cs` (the assertions to extend). Test: the composite registers
`IMediator`, `ICommandOutboxDispatcher`, `IEventBundleOutboxDispatcher`, `IMessageBus` and no
`IHostedService` whose implementation is `MediatorCommandWorker`; `AddCommandWorkerServices` still
registers the worker.

### D2 — The broker-free guarantee is stated for the composition that earns it, and proven without a broker

The guarantee holds when both bus dispatchers are replaced: the command dispatcher by
`AddStrataraOrleansCommandDispatcher` (which needs an intent store), the bundle dispatcher by a
store-reading role on the same silo. Under that composition nothing resolves `IMessageBus`: the
mediator worker is absent (D1), the dispatchers are the execution model's, the drain skips both bus
passes (`OutboxDrainWork.cs:32-49`), and `MessageRetryOptions` validates without a broker. The
integration test composes exactly the guide's command silo — `AddCommandServices`,
`AddStrataraOrleansCommandDispatcher`, `AddStrataraIntentStore`, `AddStrataraAggregateGrains`,
`AddEventProjectionServices` + `AddStrataraProjectionGrains` — and a client host with
`AddBackendServices` + the dispatcher, with no `rabbitmq` connection string and no `Messaging` section,
dispatches from the client, asserts the handler ran on the silo, the projection applied the facts,
the outbox table holds no bundle, and the silo's `IMessageBus` was never resolved (the test registers a
decorating factory that counts resolutions, or replaces the singleton with one that throws on any
member — the latter is simpler and proves more).

A silo with the command role and no store-reading role keeps the bus bundle dispatcher; the second
scenario asserts that a commit on such a silo publishes as before, so the guide's sentence is a tested
fact, not a caveat.

Evidence: `RabbitMqBus.cs:150` (lazy connection); `OrleansAggregateServiceCollectionExtensions.cs:113-131`;
`OrleansProjectionServiceCollectionExtensions.cs:154-170`; `OutboxDrainWork.cs:32-49`;
`CoHostingTests.cs` and `RoleSplitTests.cs` (the client-plus-silo shape to reuse).

### D3 — The queues are documented as an operator procedure, in the order that loses nothing

A new section *After the cut-over: the bus queues* in the migration guide replaces the last clause of
step 6. It names the queues by their subscriptions — the command subscription, the heavy-command
subscription, the event-bundle subscription of the projection workers and that of the saga workers,
as `IMessagingIdentifier` names them, each a `<subscription>.v2` quorum queue with a
`<subscription>.dead-letter` beside it — and gives the order: (1) retire every publisher to the
exchange first — for the command topic, switch every dispatching host to the execution model's
dispatcher; for the event-bundle topic, replace the worker composites and turn `hybrid` off on every
silo — because a publish to an exchange whose queues are gone is returned, stored in the outbox
table and retried by the drain for ever; (2) stop the last consumer of the queue and let it drain to
depth zero; (3) look at the dead-letter queue and decide, message by message, before emptying it —
a kept command on the bus is the bus-side counterpart of a kept command in the intent store; (4)
delete the queue and its dead-letter queue; (5) keep a queue as long as a consumer outside the
deployment subscribes to the exchange, and keep `hybrid: true` on the silos for exactly that long.
The Redis outbox lock key is named as something that needs no cleanup. The section says how to
check depth and delete (the management UI or `rabbitmqctl`) without prescribing a tool.

Evidence: `RabbitMqBus.cs:29-52`; `docs/guides/outbox-setup-rabbitmq.md:95-130,201-231` (the queue
semantics the section refers to rather than repeats); `docs/guides/migrate-to-the-orleans-execution-model.md:106-120`.

### D4 — The guide's command row is completed

The row reads: `builder.AddCommandServices()` instead, then `AddStrataraOrleansCommandDispatcher()`,
`AddStrataraIntentStore<AppWriteDbContext>()` and `AddStrataraAggregateGrains()`; and, in the *What
changes* cell, that the silo's own sends are recorded and handed over rather than published, that
the silo needs no broker once it also registers a store-reading role, and that a silo without one
keeps publishing bundles. A subsection *When the broker can go* under *Adopt the roles* states the
condition once, for the command silo and the API host. The cheat-sheet and the composition page gain
the composite's row; the `Stratara.Orleans` README's quick start uses it.

Evidence: `docs/guides/migrate-to-the-orleans-execution-model.md:178-198`;
`docs/reference/di-extensions-cheatsheet.md:22-29`; `docs/getting-started/di-composition.md:25-31`.

## Risks / Trade-offs

- [A team replaces `AddCommandWorkerServices` while its projections still read the bus] → nothing
  breaks: the bundle dispatcher is still the bus one until a store-reading role replaces it, and the
  guide says when the composite may replace the worker one.
- [A team deletes the queues before the last publisher is gone] → the guide's order puts publishers
  first and names the cost; the outbox growth is visible in the existing outbox metrics.
- [The broker-free test passes because nothing resolved the bus by chance] → the test replaces the bus
  with one that throws on every member, so any resolution that uses it fails the test, and asserts
  the outbox holds no bundle.
- [A silo with the command role only is read as broker-free] → the second scenario and the guide
  state the opposite.

## Migration Plan

Patch release. New public surface: `AddCommandServices`. No schema change; no behaviour change for a
host on the existing composites. Rollback: a host on `AddCommandServices` returns to
`AddCommandWorkerServices` and regains the worker.
