## 1. Batched renewal

- [x] 1.1 `ICommandIntentStore.RenewManyAsync` with a default over `RenewAsync`, documented (`src/Stratara.Abstractions/Abstractions/Outbox/ICommandIntentStore.cs`)
- [x] 1.2 EF store override with one statement per batch, `ExecuteUpdateAsync` over `Contains`, kept records untouched (`src/Stratara.Orleans.EntityFrameworkCore/Intents/CommandIntentStore.cs`)
- [x] 1.3 `IntentRenewals`: one loop per silo, registered by `AddStrataraAggregateGrains` beside `IntentCompletionQueue`. Leases `Hold`/`Release` their command, renewals run in chunks of 1,000 through `RenewManyAsync`, and a failed chunk is logged once. A lease built where no `IntentRenewals` is registered keeps its own timer, which the lease unit tests rely on. Disposal is idempotent, because the container disposes the instance through both registrations (`src/Stratara.Orleans/Aggregates/IntentRenewals.cs`, `IntentLease.cs`, `OrleansAggregateServiceCollectionExtensions.cs`)
- [x] 1.4 Unit tests: ten and a thousand held commands each cost one store call, more than a batch is chunked with a failed chunk logged once, a released command is not renewed, and a lease holds its command until it ends (`tests/Stratara.Orleans.Tests/IntentRenewalsTests.cs`). `IntentLeaseTests` and `IntentLeaseRenewalTests` stay green
- [x] 1.5 PostgreSQL test: `RenewManyAsync` renews exactly the listed, not-kept records (`HeavyBacklogTests.The_store_renews_a_batch_in_one_statement_and_leaves_kept_and_unlisted_records_alone`)

## 2. The bounded heavy pool and the give-backs

- [x] 2.1 `HeavyWorkOptions.QueuedLimitPerPool` (default 256), validated positive (`HeavyWorkGrain.cs`, `OrleansOptionsValidator.cs`)
- [x] 2.2 `HeavyWorkGrain.ExecuteIntentAsync` refuses beyond the bound: it gives back the attempt, logs `HeavyPoolFull` (Debug) and answers without accepting
- [x] 2.3 `IntentLease.GiveBackAsync(scopes, intentId)`. `HeavyWorkGrain`, `AggregateGrain` and `CommandRunnerGrain` give back a stamped duplicate's attempt
- [x] 2.4 Log events `HeavyPoolFull` (`117_133`) and `IntentBatchRenewalFailed` (`117_134`) (`LogEvents.cs`, `OrleansLog.cs`, `docs/reference/log-events-schema.md`)

## 3. Tests

- [x] 3.1 *A burst of heavy commands many times the cluster-wide limit*: limit 2, `QueuedLimitPerPool` 16, grace 2 s, 200 commands of 300 ms. All complete once, none kept, and `max(attempt_count)` sampled throughout never exceeds one (`tests/Stratara.Orleans.IntegrationTests/HeavyWork/HeavyBacklogTests.cs`). This test also passes on the previous code at this scale, so it guards against regression rather than proving the fix. 3.2 proves the fix
- [x] 3.2 *A hand-over finds its command still held* and the finding's chain: a store decorator fails every renewal for four graces, so records lapse and the drain hands their commands over again. On the previous code this fails with "a record reached attempt 3 while its command only waited". With the change, no record exceeds one attempt, none is kept, and all complete once (`HeavyBacklogTests.Records_that_lapse_while_their_commands_wait_are_handed_over_again_without_spending_attempts`). *A hand-over finds the pool full* is exercised by 3.1, where 200 commands meet a pool of 16
- [x] 3.3 *A silo is stopped and started during a burst*: two silos, the first stopped mid-burst. Attempts never exceed one, and every command completes once on the second (`HeavyBacklogTests.A_silo_stopped_during_a_burst_hands_it_over_without_spending_a_second_attempt`)
- [x] 3.4 *A handler keeps failing*: the existing kept-after-the-bound tests stay green (`ResumedOnceInOrderTests`, `DurableIntentTests`, in the full suite)
- [x] 3.5 *Renewals while a burst waits*: covered by 1.4

## 4. Documentation and release notes

- [x] 4.1 `docs/guides/operate-the-orleans-execution-model.md`, *A burst of heavy work*, and one sentence in `docs/concepts/orleans-execution-model.md`
- [x] 4.2 `CHANGELOG.md`: entry under `[Unreleased]`

## 5. Verification

- [x] 5.1 `openspec validate let-heavy-work-wait-without-losing-attempts --strict`
- [ ] 5.2 `./scripts/local-gauntlet.sh` green
- [ ] 5.3 `dotnet test tests/Stratara.Orleans.IntegrationTests` green (Docker)
