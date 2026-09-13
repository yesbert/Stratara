## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. Ground

- [ ] 1.1 Confirm Orleans 10.3.1 targets `net10.0`, and whether an ADO.NET grain directory and an ADO.NET
      reminder provider exist for it. Record the answer and the reminder minimum period in
      `evidence/environment.md`. Verify: the file names the package ids and versions checked.
- [ ] 1.2 Pin the Orleans packages the PoC needs in `Directory.Packages.props`, one version. Verify:
      `dotnet restore` succeeds and `git grep Microsoft.Orleans -- '*.csproj'` lists only the new projects.
- [ ] 1.3 Create `src/Stratara.Orleans/` (Tier-C, `IsPackable=false`), `tests/Stratara.Orleans.Tests/`,
      `tests/Stratara.Orleans.IntegrationTests/` and `tests/Stratara.Orleans.Benchmarks/`, and add them to
      the solution. `src/Stratara.Orleans` stays out of `Stratara.Publish.slnf`; `tests/Stratara.Orleans.Tests`
      goes in, because `PublishFilterCoverageTests` requires every `tests/*/*.Tests.csproj` to be there — the
      filter also lists test projects, and a non-packable project in it is not packed. The benchmark program
      exits without running anything unless it is given arguments, because the gauntlet runs every test-folder
      executable except `Stratara.Benchmarks`. Verify: `./scripts/local-gauntlet.sh` passes, and the only
      line added to `Stratara.Publish.slnf` is the unit-test project.

## 2. Pre-register before any run

- [ ] 2.1 Write `evidence/expectations.md`: for every correctness test and every benchmark in `design.md`,
      the expected result, a concrete falsification threshold, iteration counts, the usable-throughput
      threshold for the portable counter (D10), and the expected wall-clock duration of each run.
      Verify: committed in its own commit, before any file under `evidence/raw/` exists.
- [ ] 2.2 The owner confirms the thresholds. Verify: the confirmation date is written at the top of
      `evidence/expectations.md` in the same or a later commit, before the first raw result.

## 3. One host (stop criterion)

- [x] 3.1 Register an existing Stratara worker composite and an Orleans silo with a Redis grain directory
      in one host, in both orders. Verify: `tests/Stratara.Orleans.IntegrationTests/Hosting/CoHostingTests.cs`
      starts the host, dispatches a command through `IMediator` and calls a grain, in each order — with no
      change to any file under `src/` outside `src/Stratara.Orleans/`.

## 4. Committed position reader

- [x] 4.1 Define the port and the PoC-only store model extension (D4). Verify: the shipped
      `EventStreamEntryConfiguration.cs` is unchanged in the diff.
- [x] 4.2 Implement the naive and the safety-window readers. Verify:
      `tests/Stratara.Orleans.IntegrationTests/CommitOrder/InterleavedCommitTests.cs` shows the naive reader
      skipping an entry — if it does not, the test does not provoke the interleaving and is fixed first.
- [x] 4.3 Implement the portable counter reader. Verify: the same test passes for it over the
      pre-registered iteration count.
- [x] 4.4 Implement the PostgreSQL-native reader. Verify: the same test passes for it. Also passes the
      reversed-transaction-id case the hand-off's sketch would have failed (see design D4).
- [ ] 4.5 Optional — SQL Server native, only once every other task in groups 1-13 is done. Verify: the
      same test on a SQL Server container.

## 5. Owner-checked durable timer

- [x] 5.1 Implement the timer port over reminders, with the owner check, self-unregistration and
      cancellation in the ending turn (D6); no `IRemindable` on a public type. Verify:
      `tests/Stratara.Orleans.IntegrationTests/Timers/OwnerCheckedTimerTests.cs` — owner removed while due.
- [x] 5.2 Hard-kill test: a separately started host process with open timers is killed and restarted.
      Verify: `tests/Stratara.Orleans.IntegrationTests/Timers/HardKillTimerTests.cs` — timers fire once for
      existing owners and never for removed ones. Passed 10 of 10 kills; the run took 23 minutes, not
      the registered 5 — a silo restarted on the same endpoint waits for its predecessor to be declared
      dead before it serves reminders (noted for the results).

## 6. Singleton work

- [x] 6.1 Run a unit of work once per cluster in one grain; use the outbox drain as the example. Verify:
      `tests/Stratara.Orleans.IntegrationTests/Singleton/SingletonWorkTests.cs` — two silos, one execution
      per tick.

## 7. Aggregate grain

- [x] 7.1 Synchronous shape: an `IAggregateScopedCommand` executed in a grain keyed by aggregate id,
      through the unchanged handler under the recorded session (D5). Verify:
      `tests/Stratara.Orleans.IntegrationTests/Aggregates/ArrivalOrderTests.cs` — approve then cancel, in
      arrival order every iteration. Passed only once the send lane chained on completion: Orleans
      promises no message order (see design D5).
- [x] 7.2 Durable-intent shape behind `ICommandOutboxDispatcher`, resumed by the timer. Verify:
      `tests/Stratara.Orleans.IntegrationTests/Aggregates/DurableIntentTests.cs` — the host is killed after
      `EnqueueCommandAsync` returns and before the append; the command is applied after restart. Resumed
      by the singleton outbox drain rather than a per-intent timer: the outbox row is the intent.

## 8. Projection grain

- [x] 8.1 A grain per projection and bucket partition, reading through the port, applying through the
      existing projection pipeline, with the checkpoint in the read store's transaction and a commit hint
      plus a timer (D8, Q2, Q3). Verify: `tests/Stratara.Orleans.IntegrationTests/Projections/` covers
      idempotent apply, a genuine conflict failing, a missing prerequisite not advancing the checkpoint,
      discovery by assembly and the recorded session.
- [ ] 8.2 Kill between commit and publish, repeated, on the bus path and the checkpoint path. Verify:
      `tests/Stratara.Orleans.IntegrationTests/Projections/CommitPublishKillTests.cs` — raw counts in
      `evidence/raw/commit-publish-kill/`.

## 9. Saga grain

- [x] 9.1 Run existing stateless `ISaga` classes unchanged in a grain; add state, correlation and timeouts
      through an additional interface, with state in its own stream (Q1). Verify:
      `tests/Stratara.Orleans.IntegrationTests/Sagas/` — an unchanged saga from `tests/Stratara.Sagas.Tests`
      passes, and a stateful saga resumes its timeout after a hard kill. `ISaga.cs` is unchanged in the diff.

## 10. Heavy-work grain

- [x] 10.1 Bounded `[StatelessWorker]` grain with intent before hand-off and completion after, and a
      cluster-wide permit grain (D9, Q4). Verify:
      `tests/Stratara.Orleans.IntegrationTests/HeavyWork/HeavyBurstTests.cs` — interactive latency under a
      sustained burst stays within its pre-registered range; a crash between intent and completion is
      resumed.

## 11. Shutdown and reset

- [x] 11.1 One reset clears timers, membership and checkpoints deterministically. Verify:
      `tests/Stratara.Orleans.IntegrationTests/Hosting/ResetTests.cs` — after reset, no reminder row,
      membership row or checkpoint row remains, and a restart fires nothing.

## 12. Findings pinned

- [x] 12.1 Append the same stream version twice on the SQLite test host and record which exception
      surfaces (SF-003). Verify: a test in `tests/Stratara.Infrastructure.Tests/EventSourcing/` asserting the
      observed behaviour, named for what it pins.

## 13. Benchmarks — ask the owner before every run over 30 minutes or using a paid resource

- [ ] 13.1 Append throughput: current store, native schema, portable counter. Verify:
      `evidence/raw/append-throughput/` and a row in `evidence/results.md` against its expectation.
- [ ] 13.2 Event-to-read-model latency p50/p99: push, catch-up with hint, hybrid. Verify:
      `evidence/raw/read-model-latency/` and a row in `evidence/results.md`.
- [ ] 13.3 Commands per aggregate: `MediatorCommandWorker` against the aggregate grain, including
      contention. Verify: `evidence/raw/commands-per-aggregate/` and a row in `evidence/results.md`.
- [ ] 13.4 Rebuild duration: full replay against per-projection rebuild with other projections running.
      Verify: `evidence/raw/rebuild/` and a row in `evidence/results.md`.
- [ ] 13.5 Resource use at idle and under load. Verify: `evidence/raw/resources/` and a row in
      `evidence/results.md`.

## 14. Decide and hand over

- [ ] 14.1 Record the owner's decision for SF-001, SF-002 and SF-003 in `design.md` → *Findings*, and open a
      proposal for each that needs a fix. Verify: each finding ends in a dated decision; each fix has a
      change directory under `openspec/changes/`.
- [ ] 14.2 Write the recommendation in `evidence/results.md`: additional execution model only, or the
      recommended one, with the bus-worker deprecation path if so, and answers to Q5 and Q6. Verify: every
      claim cites a row in the results table.
- [ ] 14.3 Write the consumer migration note in `evidence/migration-note.md`: registrations that change,
      guarantees that get stronger, handler assumptions that become unnecessary but stay harmless. Verify:
      it names no consumer and points at no file outside the repository.
