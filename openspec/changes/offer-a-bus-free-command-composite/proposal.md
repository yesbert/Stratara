# Offer a bus-free command composite

> **Status:** proposed

## Why

The round-5 audit (R5-Mig-005) followed the migration guide's role table for a command silo and found
that the command role is the one role that cannot be adopted without the message broker:

- **There is no command composite without the bus-fed worker.** Projections have
  `AddEventProjectionServices` and sagas have `AddSagaServices` — the role's services without the
  hosted service that consumes the bus. Commands have only `AddCommandWorkerServices`, which registers
  the mediator worker: a hosted service that opens a broker connection at start, declares the command
  queue and consumes it beside the grains. The guide's row for the command role keeps that composite
  and adds `AddStrataraAggregateGrains()` after it, so every command silo on the execution model runs
  a bus consumer it does not need — and a silo whose broker is gone fails at start.
- **The guide's command row leaves the bus dispatcher in place.** A handler or a saga that sends a
  command through `ICommandOutboxDispatcher` on that silo publishes it to the command topic, which the
  same silo's bus worker consumes. Nothing in the row says that the execution model's dispatcher and
  the intent store belong on the command silo too; without them the silo's own sends need the broker.
- **Nothing says when a silo stops needing the broker, or what becomes of the queues.** The bus is
  registered by every composite and connected on first use; a silo whose command and bundle
  dispatchers were both replaced never touches it, but no test proves it and no page says it. The
  upgrade order ends with "delete the queues nothing consumes", without naming them, without saying
  when each may go, and without the warning that matters: a host that still publishes to an exchange
  whose queues were deleted has every publish returned, stores every bundle in the outbox table, and
  the drain retries them for ever.

## What Changes

- **A command composite without the bus-fed worker.** `AddCommandServices()` registers what
  `AddCommandWorkerServices()` registers minus the mediator worker — the common base, the mediator,
  the write store, event sourcing and the outbox dispatchers — so that a host adopting the Orleans
  execution model registers the role's services and then the execution model's registrations, and
  nothing is removed after it was registered. It is the third composite of that shape, beside
  `AddEventProjectionServices` and `AddSagaServices`.
- **A silo whose dispatchers are both replaced opens no broker connection.** A silo composed with
  `AddCommandServices`, `AddStrataraOrleansCommandDispatcher` with an intent store, and at least one
  store-reading role beside `AddStrataraAggregateGrains` runs commands, commits facts and applies them
  without a message broker configured; so does a host that only dispatches. This is verified, and the
  guide states the condition — both dispatchers replaced — and its consequence: a silo that carries the
  command role without a store-reading role still publishes bundles to the bus, because it has no
  reader to wake, and keeps the broker until it registers one.
- **The guide's command row is completed** with the dispatcher and the intent store, and
  `AddCommandServices` in place of the worker composite once no consumer of this deployment reads the
  bundles from the bus.
- **The queues after the cut-over are documented**: which queues the bus workers own (the command,
  heavy-command and event-bundle subscriptions with the dead-letter queue beside each), the order —
  stop every publisher to an exchange before deleting its queues, empty the dead-letter queue
  deliberately, delete the queue only when the last consumer is retired and the depth is zero — and
  what a publication kept after a deletion costs.
- **Consumer-visible effects:** one new composite; a new guarantee for the command silo composed
  as documented; no change for a host on the existing composites. Versioning: patch. Closes
  R5-Mig-005.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `host-composition`: *Each worker role has one composite that wires it* — command handling joins
  projection and saga orchestration as a role whose composite is also offered without its bus-fed
  worker; new scenario.
- `orleans-execution`: *The execution model can be adopted per role beside the bus workers* — a
  command silo and a dispatching host composed as documented open no broker connection; the
  documentation names the composite, the condition under which it replaces the worker composite, and
  the fate of the bus queues after the cut-over; new scenarios.

## Impact

- `Stratara.EventSourcing.WorkerDefaults` — `AddCommandServices` in
  `WorkerDefaultsHostBuilderExtensions`, `README.md`, the package description in the csproj.
- `docs/guides/migrate-to-the-orleans-execution-model.md` (the command row of *Adopt the roles*, a
  *When the broker can go* subsection, an *After the cut-over: the bus queues* section that replaces
  the last sentence of *Upgrade in this order*), `docs/reference/di-extensions-cheatsheet.md`,
  `docs/getting-started/di-composition.md`, `src/Stratara.Orleans/README.md`, `CHANGELOG.md`,
  `llms.txt`.
- Tests: `tests/Stratara.EventSourcing.WorkerDefaults.Tests/WorkerDefaultsCompositesTests.cs` (the
  composite registers no hosted worker and keeps the dispatchers); an integration test in
  `tests/Stratara.Orleans.IntegrationTests/Hosting` that composes a command silo and a client host
  without a broker and runs a command, a commit and a projection; documentation tests.
- Versioning: patch.
