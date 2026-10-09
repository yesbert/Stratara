## 1. Reproduce first

- [x] 1.1 `tests/Stratara.Infrastructure.Tests/EventSourcing/EventSourceSqliteConcurrencyTests.cs` (template:
  `SaveChangesAsync_OnSqliteDuplicateVersion_SurfacesConcurrencyException`, two scopes on one store):
  `AnAppendAfterARead_IsRecordedAfterAWriteThatLandedInBetween` — documents today's behaviour (green on
  `main`) and becomes the "option off" fact.
- [x] 1.2 Same file, red on `main` because the members do not exist yet:
  `AppendAtVersion_WhenTheStreamMovedPastIt_TheSaveThrowsConcurrencyExceptionAndRecordsNothing`,
  `AppendAtVersion_WhenTheStreamDidNotMove_RecordsFromTheNextVersion`,
  `AppendAgainstAggregatedVersion_WhenTheStreamMovedAfterTheRead_TheSaveThrowsConcurrencyException`,
  `AppendAgainstAggregatedVersion_WhenTheStreamWasCreatedAfterTheReadFoundNone_TheSaveThrowsConcurrencyException`,
  `AppendAgainstAggregatedVersion_AReadBoundedToAPastVersion_SetsNoCondition`.

## 2. The surface

- [x] 2.1 `IEventSource.AppendAtVersionAsync<TAggregate>(Guid, long, object, CancellationToken)` and
  `AppendRangeAtVersionAsync<TAggregate>(Guid, long, IEnumerable<object>, CancellationToken)`, documented
  (what the version means, the conflict at save, the refusal above the head, the `GetCurrentVersionAsync`
  hint), default implementations throwing `NotSupportedException`
  (`src/Stratara.Abstractions/Abstractions/EventSourcing/IEventSource.cs`).
- [x] 2.2 `EventSourcingOptions.AppendAgainstAggregatedVersion` (`bool`, default `false`), documented with
  what it costs; drop `[ExcludeFromCodeCoverage]` if the type gains behaviour
  (`src/Stratara.Shared/EventSourcing/EventSourcingOptions.cs`).
- [x] 2.3 `AddEventSourcing()` binds the `EventSourcing` section and registers `AggregatedStreamVersions`
  scoped; `tests/Stratara.Documentation.Tests/OptionsSectionBindingTests.cs` gets a case for the option.

## 3. The behaviour

- [x] 3.1 `AggregatedStreamVersions` (internal, scoped) in `src/Stratara.Infrastructure/EventSourcing/`.
- [x] 3.2 `AggregationService` records the head of every unbounded read (last unfiltered entry, snapshot
  version, or 0 for a missing stream) — `tests/Stratara.Infrastructure.Tests/EventSourcing/AggregationServiceTests.cs`:
  `AnUnboundedRead_RecordsTheHeadEvenWhenItsLastEventHasNoApply`,
  `ABoundedRead_RecordsNothing`, `AReadOfAMissingStream_RecordsZero`, `AReadThatEndsAtASnapshot_RecordsTheSnapshotsVersion`.
- [x] 3.3 `EventSource`: conditional staging (seed `_streamVersions` with the expected version after the
  head check; refuse above the head with `ArgumentOutOfRangeException`; refuse a second, different
  expectation for a staged stream with `InvalidOperationException`); implicit seeding from the record when
  the option is on; `ClearBatchState` clears the record —
  `tests/Stratara.Infrastructure.Tests/EventSourcing/EventSourceTests.cs`:
  `AppendAtVersion_AboveTheHead_IsRefusedAndStagesNothing`,
  `AppendAtVersion_ASecondExpectationForAStagedStream_IsRefused`,
  `AppendAtVersion_BelowTheHead_IsStagedAtTheExpectedVersion`,
  `AppendAgainstAggregatedVersion_On_DoesNotQueryTheHead`, `AppendAgainstAggregatedVersion_Off_NumbersAfterTheHead`,
  `AFailedSave_ClearsTheRememberedReadVersions` (the facts of 1.2 go green).
- [x] 3.4 PostgreSQL: `tests/Stratara.Orleans.IntegrationTests/Aggregates/ConditionalAppendTests.cs` (the
  project that owns the PostgreSQL container) — `AppendAtVersion_WhenTheStreamMovedPastIt_TheSaveThrowsConcurrencyException`
  and `AppendAgainstAggregatedVersion_WhenTheStreamMovedAfterTheRead_TheSaveThrowsConcurrencyException` on the
  Npgsql store.
- [x] 3.5 Bus redelivery, same file (the project holds both the PostgreSQL store and the RabbitMQ broker the
  RabbitMQ integration project lacks a store for):
  `AConditionalSaveThatConflicts_IsRedeliveredByTheBus_AndTheHandlerRunsAgainOnTheStreamsEnd` — a handler
  whose conditional save conflicts once is redelivered and succeeds on its second run.

## 4. Documentation

- [x] 4.1 `docs/guides/write-a-command-handler.md`, *When two writers race* (around line 147): the window
  between read and append, the two forms, the option, which handlers start to see conflicts.
- [x] 4.2 `docs/reference/di-extensions-cheatsheet.md`, the `AddEventSourcing()` row: the section it now
  binds.
- [x] 4.3 `CHANGELOG.md` → *Unreleased*: the two members, the option, the default.

## 5. Verify

- [x] 5.1 `openspec validate append-against-the-version-a-handler-read --strict`.
- [x] 5.2 `./scripts/local-gauntlet.sh` green.
- [x] 5.3 `dotnet test tests/Stratara.Orleans.IntegrationTests` and
  `dotnet test tests/Stratara.Outbox.RabbitMQ.IntegrationTests` green (Docker).
