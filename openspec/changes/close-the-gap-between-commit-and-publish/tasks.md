Ordered so that the port exists before the save uses it, the save before the dispatcher's delete,
and the measurement before the guide describes the cost.

## 1. The port and the option

- [ ] 1.1 `src/Stratara.Abstractions/Abstractions/Outbox/IEventBundleOutboxDispatcher.cs`:
      `StoresBundlesWithCommit` and `StoreEventBundleAsync(EventBundle, ITransaction, CancellationToken)`
      with default implementations (design D2), XML docs saying a consumer's dispatcher keeps
      bus-first unless it overrides both. Verify: `tests/Stratara.Abstractions.Tests` (or the nearest
      slice) proves a dispatcher that implements only the old member reports `false` and throws
      `NotSupportedException` from the store call.
- [ ] 1.2 `src/Stratara.Contracts/Messages/EventBundle.cs` (or wherever the record lives): an
      optional storage id, null on the default path. Verify: existing contract tests unchanged;
      serialisation round-trip test covers both null and set.
- [ ] 1.3 `src/Stratara.Outbox.RabbitMQ/Outbox/OutboxOptions.cs`: `DurableBundles` (default
      `false`), XML docs stating what it closes and what it costs. Verify: options test shows the
      default.

## 2. The save

- [ ] 2.1 `src/Stratara.Infrastructure/EventSourcing/EventSource.cs` `SaveChangesAsync`: when the
      dispatcher stores with the commit, map the bundle and store it under the open transaction
      before `SaveChangesAsync` on the transaction; publish after as today. Verify: a test in
      `tests/Stratara.Infrastructure.Tests/EventSourcing/` on the SQLite host with a recording
      dispatcher shows the store call received the same transaction the entries were written under,
      and that a store call that throws leaves no entries committed.

## 3. The dispatcher

- [ ] 3.1 `EventBundleOutboxDispatcher`: `StoreEventBundleAsync` writes the outbox row under the
      given transaction and stamps its id on the bundle; `EnqueueEventBundleAsync` on the durable
      path publishes and, on acceptance, deletes the row in its own short transaction, logging a
      failed delete rather than throwing (design D3); a replay in progress leaves the row (D4).
      Verify: unit tests in `tests/Stratara.Outbox.RabbitMQ.Tests` for accept-then-delete,
      refuse-then-keep, delete-fails-then-logs, replay-active-then-keep.
- [ ] 3.2 Integration test in `tests/Stratara.Outbox.RabbitMQ.IntegrationTests` with the PostgreSQL
      store: save with `DurableBundles = true` and the bus reachable → row gone after the save;
      bus stopped → row present and the save succeeds; then the drain delivers it. Verify: the three
      assertions in one test class named for the spec scenarios.
- [ ] 3.3 The kill test from the PoC, on the bus path with `DurableBundles = true`: the harness in
      `tests/Stratara.Orleans.IntegrationTests/Projections/CommitPublishKillTests.cs` gains a case
      that runs the bus scenario with the option on; 20 kills, 0 lost. Verify: the result recorded
      in this change's `evidence/` the way the PoC records it (raw JSON, environment, summary).

## 4. The cost

- [ ] 4.1 Run the PoC's append benchmark (`tests/Stratara.Orleans.Benchmarks` `--append-throughput`)
      with `DurableBundles` on and off, same hardware, three runs of 10 000; record medians in
      `evidence/` next to 3.3. Expectation written before the run: ≤ 15 % below B1's numbers at 8 and
      32 writers spread. Verify: the numbers and the pass/fail against the expectation are in
      `evidence/results.md`.

## 5. Specs, guides and changelog

- [ ] 5.1 `docs/guides/outbox-setup-rabbitmq.md` and `outbox-setup-azureservicebus.md`: the window
      on the default path, the option, the measured cost, the new steady state of the outbox table.
      Verify: both guides name `DurableBundles` and the measured percentage.
- [ ] 5.2 Regenerate `llms-full.txt` with the generator the documentation tests use. Verify: the
      documentation tests pass.
- [ ] 5.3 `CHANGELOG.md` `[Unreleased]` → *Added*: `DurableBundles` and what it closes; the interface
      members with their defaults; → *Documentation*: the window is now stated. Verify: the entry
      says the default is unchanged.

## 6. Gate

- [ ] 6.1 `./scripts/local-gauntlet.sh` green; the RabbitMQ integration suite green with Docker;
      `openspec validate close-the-gap-between-commit-and-publish --strict` clean.
