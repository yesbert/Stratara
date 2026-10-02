## 1. Reproduce first

- [x] 1.1 `tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Projections/ProjectionReplayStateTests.cs`:
  `IsReplayActive_MakesNoRequestToTheCoordinationStore` — a thousand reads leave the server's
  `INFO commandstats` count of `get` where it was (fails on `main`, which adds a thousand).
- [x] 1.2 Same file, the propagation facts, all red on `main` only in the sense that the shape does not
  exist yet: `Activate_OnOneHost_IsSeenOnAnotherWithinTheRefreshPeriod`,
  `Deactivate_OnOneHost_IsSeenOnAnother…`, `SetFailed_OnOneHost_IsSeenOnAnother…`,
  `AMarkingDeletedBehindTheHostsBack_IsSeenInactiveWithinTheRefreshPeriod` (refresh at one second,
  the key deleted directly), `Activate_IsSeenOnTheSameHostAtOnce`.

## 2. The options

- [x] 2.1 `ProjectionReplayOptions.RefreshSeconds` (default 5), documented like the lease: what a longer
  value costs, what a shorter one costs, the two bounds.
- [x] 2.2 `ProjectionReplayOptionsBinding.Validate` refuses zero or below and a value at or above the
  lease, each with a message naming `ProjectionReplay:RefreshSeconds`
  (`tests/Stratara.Outbox.RabbitMQ.Tests/DependencyInjection/ProjectionReplayOptionsBindingTests.cs`,
  three new facts).
- [x] 2.3 `tests/Stratara.Documentation.Tests/OptionsSectionBindingTests.cs`: a case for
  `RefreshSeconds` through `AddProjectionReplayState()`.

## 3. The state

- [x] 3.1 `LogEvents.Projections`: `ProjectionReplayRefreshFailed = 104_015` (warning) and
  `ProjectionReplayRefreshRecovered = 104_016` (information);
  `LoggerOutboxExtensions` gets the two source-generated methods.
- [x] 3.2 `ProjectionReplayState`: the field; `IsReplayActive` returns it; `Activate`, `Deactivate` and
  `SetFailed` set it after their write and publish on `stratara:projection:replay:state`; one
  `RefreshAsync` under a `SemaphoreSlim(1, 1)` using `StringGetAsync`, called by the subscriber's handler
  and by a `PeriodicTimer` loop over the registered `TimeProvider` that the constructor starts; a failed
  subscription retried on each tick; `IAsyncDisposable` that cancels, awaits and unsubscribes
  (the facts of 1.1 and 1.2 go green).
- [x] 3.3 `OutboxServiceCollectionExtensions.CreateProjectionReplayState` hands the state the
  `TimeProvider` it already registers and a logger; `OutboxServiceCollectionExtensionsTests` checks the
  Redis-backed state is disposed with the provider.
- [x] 3.4 `IProjectionReplayState.IsReplayActive` documentation: answered from memory, never waits on a
  coordination store, a shared marking's change seen within the refresh period; the class remarks say
  the channel is a wake-up and the refresh the guarantee.

## 3b. Found in review

- [x] 3b.1 Round 1 of the review of #187: a refresh whose read was in flight when the host made a transition
  of its own could write the stale value over it — a generation counter moves before every local transition
  and a refresh discards a read the generation overtook
  (`tests/Stratara.Outbox.RabbitMQ.Tests/Projections/ProjectionReplayStateTests.cs`,
  `A_refresh_in_flight_during_the_hosts_own_activation_does_not_overwrite_it` and the deactivation twin,
  red without the counter); a subscription that cannot be established no longer shares the refresh's
  failure flag, so a working refresh does not report a recovery every tick — it has its own pair,
  `104_017`/`104_018` (`A_subscription_that_cannot_be_established_is_logged_once_and_does_not_touch_the_refresh_log`,
  `A_refresh_that_fails_is_logged_once_per_stretch_and_its_recovery_once`).

## 4. Documentation

- [x] 4.1 `docs/guides/write-a-projection.md`, the lease section (around line 355): `RefreshSeconds`
  beside `LeaseSeconds` in both the code and the `appsettings.json` sample, the two refusals, what the
  period bounds.
- [x] 4.2 `docs/guides/operate-the-orleans-execution-model.md`: a paragraph after *A rebuild or a replay
  that does not finish* saying the model makes no blocking call to Redis on a command's or a reader's
  path, what a host can hold it to, the refresh period, and `104_015`/`104_016` in the log-id list.
- [x] 4.3 `docs/reference/di-extensions-cheatsheet.md`, the `AddProjectionReplayState()` row.
- [x] 4.4 `CHANGELOG.md` → *Unreleased*: the fix, the option, the behaviour when Redis is away, the
  rolling-upgrade note.

## 5. Verify

- [x] 5.1 `openspec validate keep-the-replay-flag-in-memory --strict`.
- [x] 5.2 `./scripts/local-gauntlet.sh` green.
- [x] 5.3 `dotnet test tests/Stratara.Outbox.RabbitMQ.IntegrationTests` and
  `dotnet test tests/Stratara.Orleans.IntegrationTests` green (Docker).
