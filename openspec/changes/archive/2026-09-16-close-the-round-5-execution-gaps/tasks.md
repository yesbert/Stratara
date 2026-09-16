## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.
      Done: approved by the owner on 2026-09-16, recorded at the owner's request.

## 1. The aggregate's order runs to the end (D1)

- [x] 1.1 `IAggregateGrain.RunAcceptedAsync` carries `[OneWay]`; the `OnlyOnFaulted` continuation and
      `AbandonAsync` stay for the undeliverable case and deactivation. Verify:
      `src/Stratara.Orleans/Aggregates/AggregateGrain.cs`.
      Done: `[OneWay]` on the interface method; the continuation and `AbandonAsync` kept, their remarks say why.
- [x] 1.2 An order longer than the response timeout runs every command in order and fails none back.
      Verify: an integration test in `tests/Stratara.Orleans.IntegrationTests/Aggregates` with
      `MessagingOptions.ResponseTimeout = 2 s` and five one-second commands on one aggregate (scenario
      *An aggregate's order takes longer than the response timeout*); run it against the 4.1.1 grain
      first and record that it fails there.
      Done: `LongOrderTests.An_order_longer_than_the_response_timeout_runs_every_command_in_order` — against the
      4.1.1 grain only 6 of 10 steps ran ("hold:0..2", the three commands behind the 2-second timeout were
      abandoned); passes now, all five in order.
- [x] 1.3 `docs/guides/operate-the-orleans-execution-model.md` names `MessagingOptions.ResponseTimeout`
      as the bound on one forwarded command and says a handler that cannot fit it is heavy work. Verify:
      the section; documentation tests.
      Done: section *The response timeout* in the operations guide. Documentation tests 699/699.

## 2. A send cycle is refused at once (D2)

- [x] 2.1 `AggregateTurn` holds the chain of turns the flow is inside; the behaviour writes it to
      `RequestContext` before forwarding and refuses a target already in the chain with a message naming
      both aggregates; the aggregate grain and the heavy grain seed their turn from the caller's chain.
      Verify: `AggregateGrain.cs`, `AggregateGrainBehavior.cs`, `HeavyWorkGrain.cs`; a unit test in
      `tests/Stratara.Orleans.Tests` on the chain and the refusal message.
      Done: `AggregateTurn` is a chain (`Chain`, `IsInside` = innermost, `Encloses` = an outer turn, `Carry`,
      `Enter(id, callerChain)`); the behaviour refuses with `CycleMessage` and carries the chain in
      `RequestContext` around the forwarded call; `ExecuteAsync` reads it into the accepted command and the run
      enters its turn behind it. Revised during apply: a recorded intent and a heavy hand-over start a fresh chain
      — nobody waits on them, so seeding them from the caller would refuse sends that cannot deadlock; the grain
      clears the carried key before its one-way self-call. `AggregateTurnTests` (5 tests).
- [x] 2.2 A→B→A fails both commands at once with the refusal, well under the response timeout. Verify:
      an integration test beside `CrossAggregateSendTests` (scenario *A handler sends back to an
      aggregate in its own chain*); recorded time under 5 s with the default timeout.
      Done: `SendCycleTests.A_send_back_into_the_chain_is_refused_at_once_naming_both_aggregates` — both handlers
      started once, the refusal names A and B, the whole cycle fails in well under a second against the default
      30-second response timeout.
- [x] 2.3 The concept page and the migration guide state the DAG rule and what a violation reports.
      Verify: `docs/concepts/orleans-execution-model.md`, `docs/guides/migrate-to-the-orleans-execution-model.md`.
      Done: concept page (*One writer per aggregate*), the command-worker row of the migration guide, and the section *Sends between aggregates* in the operations guide, which also says what to do instead (dispatch through the outbox dispatcher).

## 3. However long it runs, whether or not it yields (D3)

- [x] 3.1 `IntentLease` and `HeavyWorkGrain` start their renewal loops off the activation scheduler.
      Verify: `IntentLease.cs`, `HeavyWorkGrain.cs`; a unit test in `tests/Stratara.Orleans.Tests` that
      blocks the calling scheduler and sees renewals continue.
      Done: both renewals run from `TimeProvider.CreateTimer` timers (the conventions forbid `Task.Run`); `IntentLeaseRenewalTests.A_lease_renews_while_the_scheduler_it_was_started_on_is_blocked` — 0 renewals against the 4.1.1 lease, ≥ 2 now.
- [x] 3.2 `HeavyWorkGrain` accepts a hand-over — lease started, unit queued — and returns; a
      per-activation loop with `MaxLocalWorkers` slots runs the units under permits; `[StatelessWorker]`
      is dropped. Verify: `HeavyWorkGrain.cs`; existing `HeavyOrderTests` and `HeavyConflictTests` green.
      Done: `ExecuteIntentAsync` is `[AlwaysInterleave]`, starts the lease first, then waits for one of eight slots and a permit; held intents are not accepted twice; the grain is keyed by pool and placed by the commands role (D3/D6 revised). `HeavyOrderTests`, `HeavyConflictTests` green.
- [x] 3.3 A heavy handler that computes past the grace without awaiting runs once and keeps its permit.
      Verify: an integration test in `tests/Stratara.Orleans.IntegrationTests/HeavyWork` with
      `IntentGrace = 2 s` and a 5-second `Thread.Sleep` handler (scenario *A heavy handler computes past
      the grace without yielding*); run it against the 4.1.1 grain first and record that it fails there.
      Done: `HeavyNonYieldingTests.A_handler_that_never_yields_runs_once_and_keeps_its_permit` — against the 4.1.1 grain the handler started 3 times; once now, permit held throughout (lowest in-use while running = 1).
- [x] 3.4 `HeavyBurstTests` asserts one execution per unit with a grace shorter than the queue wait
      (scenario *A heavy burst queues commands longer than the grace*). Verify: the assertion.
      Done: the burst runs `CountedHeavyProbe` under a 2-second grace with the drain polling every 500 ms and asserts every one of the 500 units started exactly once after the outbox drained; green.
- [x] 3.5 The operations guide says the running promise holds for a non-yielding handler and for a heavy
      command waiting for a worker. Verify: the heavy-work section.
      Done: the heavy-work section of the operations guide; the concept page says it in one sentence.

## 4. Failures are logged (D4)

- [x] 4.1 `LogEvents.Orleans.IntentAttemptFailed = 117_112` and `HandOverFailed = 117_113` in
      `src/Stratara.Diagnostics/LogEvents.cs`; `OrleansLog` methods; written in `CommandExecution.RunIntentAsync`
      and in place of the `.Ignore()` calls in `OrleansCommandDispatcher`. Verify: unit tests in
      `tests/Stratara.Orleans.Tests` on both events; `IntentPipelineTests` sees the attempt logged.
      Done: `117_112` written by `IntentLease.RecordFailureAsync` with intent id, command type and aggregate id; `117_113` by `IntentHandOver.Observe` in the dispatcher and the resumer, which skips the response timeout of a call that spans a heavy or aggregate-less unit. `IntentFailureLoggingTests` (3 tests). The attempt number stays on `117_004`, where it is known.

## 5. Rebuilds do not interleave (D5)

- [x] 5.1 `ProjectionGrain` pauses by count and resumes when the count reaches zero;
      `ProjectionRebuilder.RebuildAsync` refuses while a replay is active, naming it. Verify:
      `ProjectionGrain.cs`, `ProjectionRebuilder.cs`; unit tests in `ProjectionRebuilderTests` for the
      refusal and in a grain-level test for the count.
      Done: `_pausers` count; `ResumeAsync` nudges only when it reaches zero and invalidates the cached position; `ProjectionRebuilder` refuses while `IProjectionReplayState.IsReplayActive`. `ProjectionRebuilderTests.A_rebuild_during_a_full_replay_is_refused_and_pauses_nothing`.
- [x] 5.2 Two concurrent rebuilds of one projection leave the read model complete after catch-up.
      Verify: an integration test beside `RebuildEndToEndTests` (scenario *A projection is asked to
      rebuild twice at once*); run it against the 4.1.1 grain first and record that it fails there.
      Done: `OverlappingRebuildTests.Two_overlapping_rebuilds_leave_the_model_complete` holds the second
      truncation until the readers had time to re-read after the first rebuild resumed them. Against the
      4.1.1 grain: 12 rows while the second rebuild held its truncation, 0 of 12 after it — the defect;
      passes with the pause count. `Projections` namespace 9/9.

## 6. Every role is placed by role (D6)

- [x] 6.1 A role placement generalised from `SingletonWorkPlacement`: one metadata key per role,
      published by `AddStrataraAggregateGrains`, `AddStrataraProjectionGrains`, `AddStrataraSagaGrains`,
      `AddStrataraDurableTimers` and the heavy pool's registration; one filter per role registered by
      `AddStrataraOrleans`; the grains carry their role's filter; a placement that leaves no silo fails
      with a message naming the role and its registration. Verify: `src/Stratara.Orleans/Hosting/RolePlacement.cs`
      (new), the grain classes, `StrataraOrleansSiloBuilderExtensions.cs`; unit tests on the director.
      Done: `Hosting/RolePlacement.cs`: four roles (commands, projections, sagas, timers — heavy work follows the commands role, D6 revised), one strategy and attribute per role, one director; roles published by the registrations and written into the same silo metadata as singleton work; the timers role only where an `ITimerOwners` is registered; an empty placement throws naming the role and its registration. `RolePlacementTests` (6 tests).
- [x] 6.2 Silos with different roles place every activation on a silo that registered its role. Verify:
      an integration test in `tests/Stratara.Orleans.IntegrationTests/Hosting` with one command silo and
      one projection/saga/timer silo (scenario *Silos register different roles*); run it against 4.1.1
      first and record that it fails there.
      Done: `RoleSplitTests.Every_activation_runs_on_a_silo_that_registered_its_role` — a command silo and a projection/saga/timer silo; with the filters disabled only 3 of 6 commands ran (the other aggregates landed on the readers silo); with them every command on `commands`, every projection, saga and timer on `readers`.
- [x] 6.3 A dispatch into a cluster with no command silo fails naming the role (scenario *No silo
      registers a role*). Verify: an integration test in the same class.
      Done: `RoleSplitTests.A_cluster_in_which_no_silo_registered_the_command_role_refuses_the_placement_naming_it` — the message names the commands role and `AddStrataraAggregateGrains`.
- [x] 6.4 A host with the dispatcher joins as an Orleans client and its dispatches run on the command silo
      (scenario *The API host joins as a client*). Verify: an integration test in the same class.
      Done: `RoleSplitTests.A_host_with_only_the_dispatcher_joins_as_a_client_and_its_commands_run_on_the_command_silo` — `UseOrleansClient` with ADO.NET clustering, `AddStrataraOrleansCommandDispatcher` + `AddStrataraIntentStore` on the client; all three commands ran on the command silo.
- [x] 6.5 `SingletonPlacementTests`, `CoHostingTests`, `TwoSiloKillTests` stay green. Verify: the runs.
      Done: all three in the full Orleans integration run of 76/76 (10.1).
- [x] 6.6 The migration guide says a silo that registers a role registers all of that role's handlers,
      projections or processes, that roles may be split across silos, and that the API host may be a
      client; the concept page states placement by role. Verify: the guide's role section and the concept
      page.
      Done: the *Adopt the roles* introduction and the timer row of the migration guide, the client note under *Register the silo*, the operations guide section *Placement by role*, and the concept page.

## 7. The reset names its schema and fails on an absent table (D7)

- [x] 7.1 `AddStrataraExecutionModelReset` takes an optional `schema` (default `public`); the reset
      probes schema-qualified names, throws naming an absent table, and runs the runtime deletes in one
      transaction. Verify: `ExecutionModelReset.cs`, `OrleansResetServiceCollectionExtensions.cs`;
      `SurfaceTests` updated for the parameter.
      Done: schema parameter (default `public`), schema-qualified `to_regclass` probe of all three tables before any delete, deletes in one transaction; `SurfaceTests` unchanged (no new type).
- [x] 7.2 A reset against tables in a schema removes and counts them; a reset against absent tables
      throws naming the table (scenarios *The runtime tables live in a schema*, *A runtime table is
      absent*). Verify: two integration tests beside `ResetTests`.
      Done: `ResetTests.A_reset_that_names_the_schema_clears_the_tables_there` (scripts run under `orleans_alt`, rows seeded by hand, 2 reminders + 1 membership row removed and counted) and `ResetTests.A_reset_against_absent_tables_fails_naming_the_table`; `ResetTests` 3/3.
- [x] 7.3 The operations guide and the XML example resolve the reset from a scope, and the guide says
      what a failure part-way leaves in place. Verify: `docs/guides/operate-the-orleans-execution-model.md`,
      the `<example>` on the registration.
      Done: both resolve from `CreateAsyncScope()`; the guide names the schema parameter, the absent-table failure and what a failure after the runtime deletes leaves in place.

## 8. A failed read is a stall; a cut batch keeps its checkpoint (D8)

- [x] 8.1 `StoreReaderLoop` marks the stall and logs `CatchUpFaulted` when the read or the checkpoint
      fetch throws, on every path; the partial-batch checkpoint is written with an uncancelled token.
      Verify: `StoreReaderLoop.cs`; unit tests in `StoreReaderLoopTests` (a throwing reader counts a
      stall) and `StoreReaderCancellationTests` (cancel inside a batch, checkpoint written).
      Done: the read, the checkpoint fetch and the checkpoint write are wrapped: a failure marks a read stall (a second flag beside the entry stall, one counter), logs `117_103` and rethrows; the grain no longer logs the nudge path a second time. `StoreReaderFailureTests` (stall counted via a `MeterListener` and cleared by the next good read; a cut batch writes its checkpoint with an uncancelled token).

## 9. Documentation corrected against the code (D9)

- [x] 9.1 Migration guide: the three provider packages and the location of their SQL scripts; the
      retirement trigger is the first drain silo with an intent store; the saga row names the reminder
      service; the drain silo's `MessageRetry:MaxDeliveryAttempts`. Verify: the guide; documentation tests.
      Done: the package block lists the three provider packages; the scripts are named with where they ship; the retirement trigger is the first drain silo with an intent store; the saga row names the reminder service; the outbox row names `IntentGrace` and `MaxDeliveryAttempts` as the drain silo's own settings.
- [x] 9.2 `src/Stratara.Orleans/README.md` quick start uses `AddEventProjectionServices()`;
      `src/Stratara.Orleans.EntityFrameworkCore/README.md` names `AddStrataraIntentStore`,
      `AddStrataraPortableCounterReader`, `AddStrataraExecutionModelReset` and `PartitionCounterBackfill`.
      Verify: both READMEs; doc-symbol check.
      Done: the quick start uses `AddEventProjectionServices()`; the EF README table names the portable reader registration, the backfill, the intent store and the reset.
- [x] 9.3 `docs/reference/log-events-schema.md` lists 117_001–117_113 with level and fields;
      `docs/guides/operate-the-orleans-execution-model.md` names the `orleans.*` instruments and what to
      alert on; `docs/guides/observe-the-framework.md` and `docs/guides/write-a-projection.md` say where
      checkpoints live under the execution model. Verify: the pages; documentation tests.
      Done: a table of the whole band on the reference page; *What to watch* in the operations guide with every `orleans.*` instrument and what to alert on; `observe-the-framework.md` and `write-a-projection.md` say where checkpoints and lag exist under the execution model.
- [x] 9.4 `llms.txt` and `CHANGELOG.md` `[Unreleased]`: *Fixed* for the runner, the cycle, the renewals,
      the heavy lease, the rebuild, the reset and the stall; *Changed* for placement by role and the
      logging; *Added* for the schema parameter and the two event ids. Verify: the entries.
      Done: `llms.txt` names placement by role; `llms-full.txt` regenerated; CHANGELOG *Added* (schema parameter, two events), *Changed* (placement by role, cycles refused, a failed read is a stall), *Fixed* (runner, renewals, heavy lease, rebuilds, reset resolution, cut batch, the guide).

## 10. Close

- [x] 10.1 `./scripts/local-gauntlet.sh` green; the Orleans integration namespaces touched
      (`Aggregates`, `HeavyWork`, `Projections`, `Hosting`, `Singleton`, `Timers`) green against
      PostgreSQL. Verify: the run output, recorded here.
      Done 2026-09-16/17: gauntlet green (build 0 warnings, 21 unit suites 0 failures, Orleans unit tests
      104/104, documentation tests 699/699, DocFX 0 warnings, doc-symbol check clean after `UseOrleansClient`
      joined the external names); the whole Orleans integration suite 76/76 in 21 min 38 s against
      PostgreSQL, Redis and RabbitMQ — the 67 tests of 4.1.1 plus the nine of this change.
- [x] 10.2 `openspec validate close-the-round-5-execution-gaps --strict` passes. Verify: the output.
      Done: "Change 'close-the-round-5-execution-gaps' is valid".
