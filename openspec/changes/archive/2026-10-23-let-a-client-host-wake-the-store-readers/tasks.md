## 1. The wake-up grains

- [x] 1.1 `IStoreWakeUpGrain` (integer key, `[AlwaysInterleave] WakeAsync(int[] buckets)`), as two interfaces, `IProjectionWakeUpGrain` and `ISagaWakeUpGrain`, implemented by `ProjectionWakeUpGrain` and `SagaWakeUpGrain` under their role placement filters. Both share `StoreWakeUpGrain<TTarget>.WakeLocalAsync`, which folds buckets into the silo's partitions and nudges its role's local `INudgeTarget`s (`src/Stratara.Orleans/Projections/StoreWakeUpGrain.cs`)
- [x] 1.2 Unit test: a wake-up with buckets from two partitions nudges exactly those partitions of the role's targets, under the silo's partition count rather than the caller's (`tests/Stratara.Orleans.Tests/StoreWakeUpTests.cs`, against `WakeLocalAsync`; a grain cannot be activated outside a silo)

## 2. The dispatcher wakes a set of wake-ups

- [x] 2.1 `OrleansEventBundleDispatcher` takes an optional `ClusterWakeUps` beside its targets. `ClusterWakeUps` leaves out the roles the host publishes (`PublishedRoles`) (design D3, as built) (`src/Stratara.Orleans/Projections/OrleansEventBundleDispatcher.cs`, `StoreWakeUpGrain.cs`)
- [x] 2.2 `ClusterWakeUp`: fire-and-forget with an observing continuation. A placement failure naming the role sets the role aside for `AbsentRoleRetry`, and any other failure is a lost nudge (`LogNudgeFailed`). Absent and back are logged at Information with new ids (`src/Stratara.Orleans/Diagnostics/OrleansLog.cs`, `src/Stratara.Diagnostics/LogEvents.cs`)
- [x] 2.3 Unit tests: the set-aside and its retry after the period on a `FakeTimeProvider`, an unknown grain type is not taken for an absent role, and the commit never waits for a wake-up (`tests/Stratara.Orleans.Tests/StoreWakeUpTests.cs`). `LostNudgeTests` and `HybridBundleDispatcherTests` stay green

## 3. The registration

- [x] 3.1 `StoreWakeUpOptions` (`AbsentRoleRetry`, default one minute, validated positive) and public `AddStrataraStoreWakeUps(configure, hybrid)` with full XML documentation and an example. It reuses `ReplaceBundleDispatcher`, adds the cluster wake-ups once, and publishes no role (`src/Stratara.Orleans/DependencyInjection/OrleansProjectionServiceCollectionExtensions.cs`, `src/Stratara.Orleans/Projections/StoreWakeUpOptions.cs`)
- [x] 3.2 Registration tests: called twice gives one composition; before or after `AddStrataraProjectionGrains`/`AddStrataraSagaGrains` gives the same composition; `hybrid: true` without a bus dispatcher fails naming the parameter; `hybrid: true` with one keeps it; after build it is refused (`RegistrationIdempotencyTests.cs`, `RegistrationAfterBuildTests.cs`, `StoreWakeUpTests.cs`). `SurfaceTests` lists `StoreWakeUpOptions`

## 4. Integration

- [x] 4.1 *A client host commits facts in process*: a silo with the projection role and a poll interval of minutes, and a client host with no broker, the store wake-ups and an in-process mediator handler that commits. Assert the projection applies within seconds, `outbox_entry` is empty, no publish failure is logged, and no broker connection is attempted (`tests/Stratara.Orleans.IntegrationTests/Hosting/ClientWakeUpTests.cs`)
- [x] 4.2 *A command silo without a store-reading role registers the store wake-ups* and *A silo with one store-reading role registers the store wake-ups*: role-split cluster as in `RoleSplitTests`, long poll intervals, assert both roles are woken (`ClientWakeUpTests.cs`)
- [x] 4.3 *The cluster hosts no saga role*: commits succeed, the saga role is called at most once per retry period, and a saga silo that joins later is woken after the period (`ClientWakeUpTests.cs`)
- [x] 4.4 *A client host keeps the bus beside the wake-ups*: covered in process by `StoreWakeUpTests.Asking_to_keep_the_bus_keeps_the_dispatcher_registered_before` against a recording bus dispatcher
- [x] 4.5 *A silo commits without a store-reading role*: the existing test stays green unchanged (`BrokerFreeSiloTests.cs`)

## 5. Documentation and release notes

- [x] 5.1 `docs/guides/migrate-to-the-orleans-execution-model.md`: rewrite the paragraph above the broker table and the table. An API host that commits in process needs `AddStrataraStoreWakeUps()` or the broker. A command silo without a store-reading role can drop the broker with it. Add an example composition for an API host
- [x] 5.2 `docs/guides/operate-the-orleans-execution-model.md`: a short section on what a wake-up is, what an absent role looks like in the log, and that a lost wake-up costs at most the poll interval
- [x] 5.3 `docs/concepts/orleans-execution-model.md` and `llms.txt`: name the registration where the broker-free composition is described
- [x] 5.4 `CHANGELOG.md`: entry under `[Unreleased]`

## 6. Verification

- [x] 6.1 `openspec validate let-a-client-host-wake-the-store-readers --strict`
- [x] 6.2 `./scripts/local-gauntlet.sh` green
- [x] 6.3 `dotnet test tests/Stratara.Orleans.IntegrationTests` green for the new tests and `BrokerFreeSiloTests`, `RoleSplitTests` (Docker)
