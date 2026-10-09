## 1. Reproduce first

- [ ] 1.1 `tests/Stratara.Infrastructure.Tests/EventSourcing/EventSourceTests.cs`: the existing
  `CreateAsync_NewStream_FallsBackToTheSessionWhenTheEventNamesNoTenant` (line 685) stays green and is the
  `Allow` fact; new, red until the members exist:
  `CreateOnBehalfOfAsync_RecordsTheStatedOwnerForTheStreamsLaterEvents`,
  `CreateOnBehalfOfAsync_OnAnExistingStream_FailsNamingTheStream`,
  `CreateOnBehalfOfAsync_WithAnEmptyTenant_FailsAndStagesNothing`,
  `NewStreamOwnerFromSession_Warn_RecordsTheSessionOwnerAndLogs102104Once`,
  `NewStreamOwnerFromSession_Refuse_FailsBeforeStaging`,
  `NewStreamOwnerFromSession_Refuse_LeavesStatedOwnersCreationEventsAndExistingStreamsAlone`,
  `NewStreamOwnerFromSession_Refuse_AnExistingStreamWithoutARecordedTenant_StillTakesTheSession`.

## 2. The surface

- [ ] 2.1 `IEventSource.CreateOnBehalfOfAsync<TAggregate>(Guid, object, EventSubject, CancellationToken)`,
  documented, with the default implementation of the design; the interface remarks on the owner chain
  name the policy (`src/Stratara.Abstractions/Abstractions/EventSourcing/IEventSource.cs`).
- [ ] 2.2 `NewStreamOwnerPolicy` (`Allow`, `Warn`, `Refuse`) and
  `EventSourcingOptions.NewStreamOwnerFromSession` (default `Allow`), documented
  (`src/Stratara.Shared/EventSourcing/`); `tests/Stratara.Documentation.Tests/OptionsSectionBindingTests.cs`
  gets a case binding `Refuse`.

## 3. The behaviour

- [ ] 3.1 `EventSource.CreateOnBehalfOfAsync`; `ResolveSubjectAsync` takes the stream version and applies the
  policy at the session step — the facts of 1.1 go green.
- [ ] 3.2 `LogEvents.EventStore.NewStreamOwnerTakenFromSession = 102_104` and the source-generated method on
  `EventSource`.
- [ ] 3.3 A real-store fact on SQLite through `EventStoreTestHost`
  (`tests/Stratara.Infrastructure.Tests/EventSourcing/EventSourceOwningUserTests.cs`):
  `CreateOnBehalfOfAsync_TheStoredEntriesCarryTheStatedTenant`.

## 4. Documentation

- [ ] 4.1 `docs/guides/write-a-command-handler.md`, the owner chain (around line 70) and
  `AppendOnBehalfOfAsync` (around line 119): `CreateOnBehalfOfAsync`, the policy, the `Warn`-then-`Refuse`
  rollout.
- [ ] 4.2 `CHANGELOG.md` → *Unreleased*.

## 5. Verify

- [ ] 5.1 `openspec validate state-the-owner-of-a-new-stream --strict`.
- [ ] 5.2 `./scripts/local-gauntlet.sh` green.
