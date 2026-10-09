## 1. Reproduce first

- [ ] 1.1 `tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Projections/ProjectionReplayStateTests.cs`:
  `GetProgress_AfterASucceededReplay_ReportsItsOutcome` and
  `GetProgress_AfterAFailedReplay_ReportsZeroCountersAndAFailedOutcome` (red on `main`: no outcome, and
  the Redis counters survive `SetFailed`).
- [ ] 1.2 `tests/Stratara.Projections.Tests/Services/ProjectionReplayWorkerTests.cs`:
  `ReplayCallback_CancelledBetweenBatches_CompletesAsInterruptedAndDoesNotLogCompleted` (red on `main`,
  which logs `104_006`).
- [ ] 1.3 Two hosts on one Redis: `tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Projections/ProjectionReplayStateTests.cs`
  `ARequestSeenByTwoHosts_IsClaimedByOne` and `ARequestSeenAfterAShortReplayEnded_IsNotRunAgain` (red on
  `main`: no claim exists).

## 2. The surface

- [ ] 2.1 `ReplayResult`, `ReplayOutcome`, `ReplayProgress.RequestId` and `.LastReplay` (`init`), each
  documented (`src/Stratara.Abstractions/Abstractions/Projections/IProjectionReplayState.cs`).
- [ ] 2.2 `RequestReplay(Guid)`, `SubscribeToReplayRequestAsync(Func<Guid, Task>, CancellationToken)`,
  `TryActivate(Guid)`, `Complete(ReplayResult, long, string?)` with the default implementations of the
  design; `tests/Stratara.Projections.Tests/Services/ProjectionReplayStateDefaultsTests.cs` (new — there is
  no test project for `Stratara.Abstractions`): a minimal implementer relying on the defaults activates,
  refuses while active, and completes through `Deactivate`/`SetFailed`.

## 3. The states

- [ ] 3.1 `InProcessProjectionReplayState`: request id, start, claim (a claimed-id set under the lock),
  outcome, counters 0/0 once ended —
  `tests/Stratara.Outbox.RabbitMQ.Tests/Projections/InProcessProjectionReplayStateTests.cs`:
  `TryActivate_TheSameRequestTwice_RunsItOnce`, `TryActivate_WhileActive_IsRefused`,
  `Complete_KeepsTheOutcomeUntilTheNextCompletes`, `Complete_StampsStartAndEndFromTheTimeProvider`.
- [ ] 3.2 `ProjectionReplayState` (Redis): the claim script, the `…:last` hash, the id on the request
  channel, the legacy `"replay"` payload, `GetProgress` — the facts of 1.1 and 1.3 go green;
  `RequestReplay_WithAnId_DeliversThatId`, `ALegacyRequestPayload_IsRunUnderAFreshId`.
- [ ] 3.3 `LogEvents.Projection`: `ProjectionReplayRequestNotRun = 104_019`,
  `ProjectionReplayInterrupted = 104_020`; the two source-generated methods in
  `LoggerProjectionExtensions`.

## 4. The worker

- [ ] 4.1 `ProjectionReplayWorker`: subscribe with the id; `TryActivate` or return; track the replayed
  count; `Complete` with `Succeeded`, `Failed` (message truncated as today) or `Interrupted`; no
  `Deactivate` in `finally` — the facts of 1.2 go green; existing facts updated where they assert
  `Deactivate`/`SetFailed` calls (`ReplayCallback_FailureInReplay_CallsSetFailedWithExceptionMessage` →
  `…_CompletesAsFailedWithTheMessage`).
- [ ] 4.2 The hand-written doubles (`tests/Stratara.Projections.Tests/Services/ProjectionReplayStreamOrderTests.cs`
  `AwaitedReplayState`, its Orleans twin, `tests/Stratara.Orleans.IntegrationTests/Aggregates/ResumedOnceInOrderTests.cs`
  `SwitchedReplay`) compile unchanged and their tests stay green.

## 5. Documentation

- [ ] 5.1 `docs/guides/write-a-projection.md`, *Watch a replay* (around line 403): the table gets the
  outcome rows and the after-failure counters read 0/0 on both states; the endpoint sample requests with
  an id and returns it; a paragraph on one-request-one-replay and on `104_019`/`104_020`.
- [ ] 5.2 `CHANGELOG.md` → *Unreleased*: the outcome, the identity, the claim (and the double run it ends),
  the interrupted ending, the counter alignment, the rolling-upgrade note.

## 6. Verify

- [ ] 6.1 `openspec validate keep-the-outcome-of-the-last-replay --strict`.
- [ ] 6.2 `./scripts/local-gauntlet.sh` green.
- [ ] 6.3 `dotnet test tests/Stratara.Outbox.RabbitMQ.IntegrationTests` and
  `dotnet test tests/Stratara.Orleans.IntegrationTests` green (Docker).
