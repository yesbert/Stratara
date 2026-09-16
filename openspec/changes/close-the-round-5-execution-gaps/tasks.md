## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. The aggregate's order runs to the end (D1)

- [ ] 1.1 `IAggregateGrain.RunAcceptedAsync` carries `[OneWay]`; the `OnlyOnFaulted` continuation and
      `AbandonAsync` stay for the undeliverable case and deactivation. Verify:
      `src/Stratara.Orleans/Aggregates/AggregateGrain.cs`.
- [ ] 1.2 An order longer than the response timeout runs every command in order and fails none back.
      Verify: an integration test in `tests/Stratara.Orleans.IntegrationTests/Aggregates` with
      `MessagingOptions.ResponseTimeout = 2 s` and five one-second commands on one aggregate (scenario
      *An aggregate's order takes longer than the response timeout*); run it against the 4.1.1 grain
      first and record that it fails there.
- [ ] 1.3 `docs/guides/operate-the-orleans-execution-model.md` names `MessagingOptions.ResponseTimeout`
      as the bound on one forwarded command and says a handler that cannot fit it is heavy work. Verify:
      the section; documentation tests.

## 2. A send cycle is refused at once (D2)

- [ ] 2.1 `AggregateTurn` holds the chain of turns the flow is inside; the behaviour writes it to
      `RequestContext` before forwarding and refuses a target already in the chain with a message naming
      both aggregates; the aggregate grain and the heavy grain seed their turn from the caller's chain.
      Verify: `AggregateGrain.cs`, `AggregateGrainBehavior.cs`, `HeavyWorkGrain.cs`; a unit test in
      `tests/Stratara.Orleans.Tests` on the chain and the refusal message.
- [ ] 2.2 A→B→A fails both commands at once with the refusal, well under the response timeout. Verify:
      an integration test beside `CrossAggregateSendTests` (scenario *A handler sends back to an
      aggregate in its own chain*); recorded time under 5 s with the default timeout.
- [ ] 2.3 The concept page and the migration guide state the DAG rule and what a violation reports.
      Verify: `docs/concepts/orleans-execution-model.md`, `docs/guides/migrate-to-the-orleans-execution-model.md`.

## 3. However long it runs, whether or not it yields (D3)

- [ ] 3.1 `IntentLease` and `HeavyWorkGrain` start their renewal loops off the activation scheduler.
      Verify: `IntentLease.cs`, `HeavyWorkGrain.cs`; a unit test in `tests/Stratara.Orleans.Tests` that
      blocks the calling scheduler and sees renewals continue.
- [ ] 3.2 `HeavyWorkGrain` accepts a hand-over — lease started, unit queued — and returns; a
      per-activation loop with `MaxLocalWorkers` slots runs the units under permits; `[StatelessWorker]`
      is dropped. Verify: `HeavyWorkGrain.cs`; existing `HeavyOrderTests` and `HeavyConflictTests` green.
- [ ] 3.3 A heavy handler that computes past the grace without awaiting runs once and keeps its permit.
      Verify: an integration test in `tests/Stratara.Orleans.IntegrationTests/HeavyWork` with
      `IntentGrace = 2 s` and a 5-second `Thread.Sleep` handler (scenario *A heavy handler computes past
      the grace without yielding*); run it against the 4.1.1 grain first and record that it fails there.
- [ ] 3.4 `HeavyBurstTests` asserts one execution per unit with a grace shorter than the queue wait
      (scenario *A heavy burst queues commands longer than the grace*). Verify: the assertion.
- [ ] 3.5 The operations guide says the running promise holds for a non-yielding handler and for a heavy
      command waiting for a worker. Verify: the heavy-work section.

## 4. Failures are logged (D4)

- [ ] 4.1 `LogEvents.Orleans.IntentAttemptFailed = 117_112` and `HandOverFailed = 117_113` in
      `src/Stratara.Diagnostics/LogEvents.cs`; `OrleansLog` methods; written in `CommandExecution.RunIntentAsync`
      and in place of the `.Ignore()` calls in `OrleansCommandDispatcher`. Verify: unit tests in
      `tests/Stratara.Orleans.Tests` on both events; `IntentPipelineTests` sees the attempt logged.

## 5. Rebuilds do not interleave (D5)

- [ ] 5.1 `ProjectionGrain` pauses by count and resumes when the count reaches zero;
      `ProjectionRebuilder.RebuildAsync` refuses while a replay is active, naming it. Verify:
      `ProjectionGrain.cs`, `ProjectionRebuilder.cs`; unit tests in `ProjectionRebuilderTests` for the
      refusal and in a grain-level test for the count.
- [ ] 5.2 Two concurrent rebuilds of one projection leave the read model complete after catch-up.
      Verify: an integration test beside `RebuildEndToEndTests` (scenario *A projection is asked to
      rebuild twice at once*); run it against the 4.1.1 grain first and record that it fails there.

## 6. Every role is placed by role (D6)

- [ ] 6.1 A role placement generalised from `SingletonWorkPlacement`: one metadata key per role,
      published by `AddStrataraAggregateGrains`, `AddStrataraProjectionGrains`, `AddStrataraSagaGrains`,
      `AddStrataraDurableTimers` and the heavy pool's registration; one filter per role registered by
      `AddStrataraOrleans`; the grains carry their role's filter; a placement that leaves no silo fails
      with a message naming the role and its registration. Verify: `src/Stratara.Orleans/Hosting/RolePlacement.cs`
      (new), the grain classes, `StrataraOrleansSiloBuilderExtensions.cs`; unit tests on the director.
- [ ] 6.2 Silos with different roles place every activation on a silo that registered its role. Verify:
      an integration test in `tests/Stratara.Orleans.IntegrationTests/Hosting` with one command silo and
      one projection/saga/timer silo (scenario *Silos register different roles*); run it against 4.1.1
      first and record that it fails there.
- [ ] 6.3 A dispatch into a cluster with no command silo fails naming the role (scenario *No silo
      registers a role*). Verify: an integration test in the same class.
- [ ] 6.4 A host with the dispatcher joins as an Orleans client and its dispatches run on the command silo
      (scenario *The API host joins as a client*). Verify: an integration test in the same class.
- [ ] 6.5 `SingletonPlacementTests`, `CoHostingTests`, `TwoSiloKillTests` stay green. Verify: the runs.
- [ ] 6.6 The migration guide says a silo that registers a role registers all of that role's handlers,
      projections or processes, that roles may be split across silos, and that the API host may be a
      client; the concept page states placement by role. Verify: the guide's role section and the concept
      page.

## 7. The reset names its schema and fails on an absent table (D7)

- [ ] 7.1 `AddStrataraExecutionModelReset` takes an optional `schema` (default `public`); the reset
      probes schema-qualified names, throws naming an absent table, and runs the runtime deletes in one
      transaction. Verify: `ExecutionModelReset.cs`, `OrleansResetServiceCollectionExtensions.cs`;
      `SurfaceTests` updated for the parameter.
- [ ] 7.2 A reset against tables in a schema removes and counts them; a reset against absent tables
      throws naming the table (scenarios *The runtime tables live in a schema*, *A runtime table is
      absent*). Verify: two integration tests beside `ResetTests`.
- [ ] 7.3 The operations guide and the XML example resolve the reset from a scope, and the guide says
      what a failure part-way leaves in place. Verify: `docs/guides/operate-the-orleans-execution-model.md`,
      the `<example>` on the registration.

## 8. A failed read is a stall; a cut batch keeps its checkpoint (D8)

- [ ] 8.1 `StoreReaderLoop` marks the stall and logs `CatchUpFaulted` when the read or the checkpoint
      fetch throws, on every path; the partial-batch checkpoint is written with an uncancelled token.
      Verify: `StoreReaderLoop.cs`; unit tests in `StoreReaderLoopTests` (a throwing reader counts a
      stall) and `StoreReaderCancellationTests` (cancel inside a batch, checkpoint written).

## 9. Documentation corrected against the code (D9)

- [ ] 9.1 Migration guide: the three provider packages and the location of their SQL scripts; the
      retirement trigger is the first drain silo with an intent store; the saga row names the reminder
      service; the drain silo's `MessageRetry:MaxDeliveryAttempts`. Verify: the guide; documentation tests.
- [ ] 9.2 `src/Stratara.Orleans/README.md` quick start uses `AddEventProjectionServices()`;
      `src/Stratara.Orleans.EntityFrameworkCore/README.md` names `AddStrataraIntentStore`,
      `AddStrataraPortableCounterReader`, `AddStrataraExecutionModelReset` and `PartitionCounterBackfill`.
      Verify: both READMEs; doc-symbol check.
- [ ] 9.3 `docs/reference/log-events-schema.md` lists 117_001–117_113 with level and fields;
      `docs/guides/operate-the-orleans-execution-model.md` names the `orleans.*` instruments and what to
      alert on; `docs/guides/observe-the-framework.md` and `docs/guides/write-a-projection.md` say where
      checkpoints live under the execution model. Verify: the pages; documentation tests.
- [ ] 9.4 `llms.txt` and `CHANGELOG.md` `[Unreleased]`: *Fixed* for the runner, the cycle, the renewals,
      the heavy lease, the rebuild, the reset and the stall; *Changed* for placement by role and the
      logging; *Added* for the schema parameter and the two event ids. Verify: the entries.

## 10. Close

- [ ] 10.1 `./scripts/local-gauntlet.sh` green; the Orleans integration namespaces touched
      (`Aggregates`, `HeavyWork`, `Projections`, `Hosting`, `Singleton`, `Timers`) green against
      PostgreSQL. Verify: the run output, recorded here.
- [ ] 10.2 `openspec validate close-the-round-5-execution-gaps --strict` passes. Verify: the output.
