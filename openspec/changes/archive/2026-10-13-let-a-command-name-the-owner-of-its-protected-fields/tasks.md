## 1. Reproduce first

- [x] 1.1 `tests/Stratara.Infrastructure.Tests/Security/CommandOwnerProtectionTests.cs`:
  `ACommandNamingNoOwner_FromASessionWithoutATenant_IsRefusedOutsideDevelopment` — pins the finding
  with the real serializer, key store, dispatcher and a non-development environment (green on `main`;
  it is the behaviour a command that names no owner keeps). The real serializer is internal to
  `Stratara.Infrastructure`, so the end-to-end facts live in its test project rather than in
  `Stratara.Outbox.RabbitMQ.Tests`.
- [x] 1.2 Same file: `ACommandNamingAnOwner_FromASessionWithoutATenant_ReachesItsHandlerWithItsProtectedField`
  (does not compile on `main`: `INamesDataOwner` does not exist).
- [x] 1.3 `tests/Stratara.WriteStore.Tests/CommandAuditRepositoryTests.cs`:
  `AddAsync_CommandNamingNone_RecordsForTheSessionsTenantAndUser` — pins the silent case on `main`
  for a command that names no owner (green on `main`, stays green).

## 2. The surface

- [x] 2.1 `src/Stratara.Abstractions/Security/INamesDataOwner.cs` — `DataOwnerTenantId`,
  `DataOwnerUserId` with its `null` default, XML documentation with the explicit-implementation example
  and the sentence that it authorizes nothing (design D1).
- [x] 2.2 `src/Stratara.Contracts/Messages/CommandEnvelope.cs` — the two init-only properties, omitted
  when null (design D4). `tests/Stratara.Shared.Tests/Messaging/CommandEnvelopeShapeTests.cs` (new):
  `Serialize_EnvelopeNamingNoOwner_IsByteIdenticalToTheFormBeforeTheOwner` (against a literal),
  `Deserialize_EnvelopeWrittenBeforeTheOwner_NamesNone`, `RoundTrip_EnvelopeNamingAnOwner_KeepsTenantAndUser`,
  `Serialize_EnvelopeNamingATenantAlone_WritesNoUser`, `Constructor_AndDeconstruct_KeepTheirSixParameters`.
- [x] 2.3 `src/Stratara.Shared/Security/CommandDataOwner.cs` — the resolution of design D3 and the
  refusal of design D2. `tests/Stratara.Shared.Tests/Security/CommandDataOwnerTests.cs` (new):
  `ForTransit_CommandNamingAnOwner_ReturnsThatOwner`, `ForTransit_CommandNamingATenantAlone_NamesNoUser`,
  `ForTransit_CommandNamingNone_ReturnsSessionTenantAndActorUser`,
  `ForRecord_CommandNamingAnOwner_ReturnsThatOwner`, `ForRecord_CommandNamingNone_ReturnsSessionTenantAndUser`,
  `ForTransit_AndForRecord_AgreeForACommandNamingAnOwner`,
  `ForTransit_CommandNamingAnEmptyTenant_ThrowsNamingTheCommandType`,
  `ForRecord_CommandNamingAnEmptyTenant_ThrowsNamingTheCommandType`,
  `ForRead_EnvelopeNamingAnOwner_ReturnsIt`, `ForRead_EnvelopeNamingNone_ReturnsSessionTenantAndActorUser`,
  `ForRead_AUserWithoutATenant_IsReadForTheSession`,
  `NamedTenantIdAndUserId_AreTheOwnerOnlyWhereTheCommandNamedIt`, `NullArguments_Throw`.

## 3. The signature

- [x] 3.1 `src/Stratara.Abstractions/Messaging/BusEnvelopeCanonical.cs` — append owner tenant and owner
  user when the envelope names an owner (design D5).
  `tests/Stratara.Shared.Tests/Messaging/BusEnvelopeCanonicalTests.cs`:
  `Of_CommandEnvelopeNamingNoOwner_IsTheProjectionBeforeTheOwner` (against the literal projection),
  `Of_CommandEnvelope_CoversTheNamedOwnersTenant`, `Of_CommandEnvelope_CoversTheNamedOwnersUser`,
  `Of_CommandEnvelope_RemovingTheNamedOwnerProducesDifferentCanonical`,
  `Of_CommandEnvelopeNamingAnOwner_SurvivesAJsonRoundTrip`. The guard
  `TheSignedRecordsHaveTheFieldsTheCanonicalFormAccountsFor` now reads the records' public properties
  instead of their constructor parameters, so a field added as a property — as the owner is — is caught
  as one added as a parameter was.
- [x] 3.2 `tests/Stratara.Outbox.RabbitMQ.Tests/Mediator/MediatorCommandWorkerTests.cs`:
  `DispatchAsync_StrictMode_SignedEnvelopeNamingAnOwner_Dispatches`,
  `DispatchAsync_StrictMode_OwnerAlteredAfterSigning_IsRejected` and
  `DispatchAsync_StrictMode_OwnerRemovedAfterSigning_IsRejected`.

## 4. The bus path

- [x] 4.1 `src/Stratara.Shared/Mediator/Mapping/CommandEnvelopeMapper.cs` — encrypt for
  `CommandDataOwner`'s transit pair and set the envelope's owner when the command names one; XML
  documentation rewritten to the new rule. `tests/Stratara.Shared.Tests/Mediator/Mapping/CommandEnvelopeMapperTests.cs`
  (new): `MapToAsync_CommandNamingAnOwner_SerializesForThatOwnerAndStampsTheEnvelope`,
  `MapToAsync_CommandNamingATenantAlone_StampsNoUser`,
  `MapToAsync_CommandNamingNone_SerializesForSessionTenantAndActorUserAndStampsNothing`,
  `MapToAsync_CommandNamingAnOwner_CarriesTheSessionUnchanged`,
  `MapToAsync_CommandNamingAnEmptyTenant_Throws`.
- [x] 4.2 `src/Stratara.Outbox.RabbitMQ/Mediator/MediatorCommandWorker.cs` — read for
  `CommandDataOwner`'s read pair. `MediatorCommandWorkerTests.cs`:
  `DispatchAsync_EnvelopeNamingAnOwner_ReadsForThatOwnerAndRunsUnderTheDispatchingSession`,
  `DispatchAsync_EnvelopeNamingATenantAlone_ReadsForThatTenantAndNoUser`,
  `DispatchAsync_EnvelopeNamingNone_ReadsForSessionTenantAndActorUser`.
- [x] 4.3 1.2 is green. `tests/Stratara.Outbox.RabbitMQ.Tests/Outbox/CommandOutboxDispatcherTests.cs`:
  `EnqueueCommandAsync_CommandNamingAnOwner_PublishesAnEnvelopeCarryingIt`,
  `EnqueueCommandAsync_CommandNamingAnOwner_StoresAnEnvelopeCarryingItWhenTheBusIsDown`,
  `EnqueueCommandAsync_CommandNamingNone_PublishesAnEnvelopeWithoutAnOwner`,
  `EnqueueCommandAsync_CommandNamingAnEmptyTenant_ThrowsAndSendsNothing`,
  `EnqueueOutboxEntriesAsync_StoredCommandNamingAnOwner_IsRepublishedWithItsOwner` (the republished
  envelope equals the stored one and its signature still covers it).
  `CommandOwnerProtectionTests.cs`: `ACommandNamingAnOwner_FromAnotherTenantsSession_IsWrittenForTheNamedTenantAlone`,
  `ACommandNamingNoOwner_IsWrittenForTheSessionsTenantAndActingUser`,
  `InStrictMode_ACommandNamingAnOwner_VerifiesAndReachesItsHandler`,
  `InStrictMode_ACommandWhoseOwnerWasAlteredOnTheWire_IsNotHandled`.
- [x] 4.4 `CommandOwnerProtectionTests.cs`:
  `AHostThatReadsForTheSession_FailsACommandNamingAnotherOwner_RatherThanEmptyingItsField` — the
  evidence for what an earlier host does with an owner-naming command (design, *Risks*), kept beside
  the dispatch it reads rather than in `SecureJsonSerializerTests.cs`.

## 5. The recorded copy

- [x] 5.1 `src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/Repositories/CommandAuditRepository.cs`
  — encrypt for `CommandDataOwner`'s record pair; `TenantId`/`UserId` from it, `Actor*` from the session
  (design D7); remarks rewritten. `tests/Stratara.WriteStore.Tests/CommandAuditRepositoryTests.cs`:
  `AddAsync_CommandNamingAnOwner_EncryptsForAndAttributesToThatOwner`,
  `AddAsync_CommandNamingAnOwner_KeepsTheSessionsActor`,
  `AddAsync_CommandNamingAnOwnerAndUser_WritesForBoth`,
  `AddAsync_CommandNamingAnEmptyTenant_ThrowsAndAddsNoRow`.
- [x] 5.2 `tests/Stratara.EventSourcing.Pipeline.CommandAudit.Tests/CommandAuditBehaviorTests.cs`:
  `HandleAsync_CommandTheRecordRefuses_DoesNotRunTheHandlerAndSavesNothing`.
- [x] 5.3 `tests/Stratara.WriteStore.Tests/CommandRecordOwnerErasureTests.cs` (new — the audit
  repository with the real serializer and the test key store; the project gains references to
  `Stratara.Infrastructure` and `Stratara.Testing` for it):
  `TheRecordOfACommandNamingAnOwner_FromASessionWithoutATenant_ReadsBackFromItsOwnColumns`,
  `TheOwnersErasure_LeavesTheRecordAndEmptiesItsProtectedField`,
  `TheErasureOfTheDispatchingSessionsTenant_DoesNotReachTheRecordOfACommandNamingAnotherOwner`,
  `BothCopiesOfACommandNamingAnOwner_AreWrittenUnderOneKey` (the two wrappers carry the same key id).
  The travelling copy: `CommandOwnerProtectionTests.cs`
  `TheOwnersErasure_WhileTheCommandWaits_DeliversItWithTheFieldAbsent`,
  `TheErasureOfTheDispatchingSessionsTenant_DoesNotReachACommandNamingAnotherOwner`.

## 6. The Orleans path

- [x] 6.1 `src/Stratara.Orleans/Aggregates/AggregateCommandEnvelope.cs` — `[Id(4)]`, `[Id(5)]`;
  `IntentRecorder.RecordAsync`, `AggregateGrainBehavior.BuildEnvelopeAsync` write through
  `CommandDataOwner`; the resumer in `OrleansCommandDispatcher.cs` passes the owner of the signed
  envelope on; `CommandExecution.InvokeAsync` in `AggregateGrain.cs` reads for it.
- [x] 6.2 `tests/Stratara.Orleans.Tests/IntentRecordIntegrityTests.cs`:
  `A_recorded_command_naming_an_owner_whose_owner_was_changed_in_the_record_is_kept_under_strict_mode`
  (altered and removed); `tests/Stratara.Orleans.Tests/CommandOwnerTests.cs` (new):
  `A_recorded_command_naming_an_owner_is_recorded_and_handed_over_for_that_owner`,
  `A_recorded_command_naming_no_owner_is_recorded_and_handed_over_as_before`,
  `A_recorded_command_naming_an_owner_is_signed_over_its_owner`,
  `A_command_naming_an_empty_tenant_is_refused_and_nothing_is_recorded`,
  `A_resumed_command_is_handed_over_with_the_owner_its_record_names`,
  `A_resumed_command_naming_no_owner_is_handed_over_without_one`,
  `A_forwarded_command_naming_an_owner_is_written_for_that_owner`,
  `A_forwarded_command_naming_no_owner_is_written_as_before`,
  `A_forwarded_command_naming_an_empty_tenant_is_refused_before_it_reaches_its_activation`,
  `An_activation_reads_a_command_for_the_owner_its_envelope_names_and_runs_under_the_carried_session`,
  `An_activation_reads_a_command_naming_no_owner_for_the_session`.
- [x] 6.3 `tests/Stratara.Orleans.IntegrationTests/Aggregates/ResumedCommandOwnerTests.cs` (new, needs
  Docker): `A_resumed_command_naming_an_owner_from_a_session_without_a_tenant_reaches_its_handler_with_its_protected_field`
  — the record row on PostgreSQL carries the owner and not the plaintext, the drain resumes it, the
  handler receives the field and runs under the recorded session. The claim helper in
  `ResumedOnceInOrderTests.cs` carries the owner as a resumption does.

## 7. Documentation

- [x] 7.1 XML documentation that states the old rule: `ISubjectEraser` (*what it does not cover* — the
  rows stay, an owner-naming command's protected fields die with the owner's keys),
  `BusEnvelopeIntegrityOptions` and `BusEnvelopeCanonical` (what the canonical form covers),
  `CommandEnvelope` (the two properties), `CommandEnvelopeMapper`, `CommandAuditRepository`,
  `AggregateCommandEnvelope`. `IBusEnvelopeSigner` states the coverage as "every field except the
  signature" and needed no change.
- [x] 7.2 `SECURITY.md` — the list of what the signature covers gains the named owner.
- [x] 7.3 `docs/guides/write-a-command-handler.md` — new section *Whose key a command's protected
  fields are under*: the declaration, the explicit implementation, that it authorizes nothing, the
  empty owner, a command arriving after its owner is gone, the rolling-upgrade order;
  `docs/concepts/tenant-aware-encryption.md` — the owner a value is sealed for, by what is sealed;
  `docs/guides/encrypt-data-setup.md` — a session without a tenant, and the AAD's tenant;
  `docs/guides/hmac-bus-envelope.md` — the owner in *What's signed* and the upgrade order;
  `docs/concepts/session-context.md` — naming the owner instead of promoting the session.
- [x] 7.4 `llms.txt`, `CHANGELOG.md` (*Unreleased*: the declaration, the envelope properties, the
  upgrade order, and the divergence of design D6 stated as it is). `llms-full.txt` is generated from
  the assemblies' registrations, options and exceptions, none of which change; the build that
  regenerates it reports no difference.
- [x] 7.5 `dotnet test tests/Stratara.Documentation.Tests` — 786 passed.

## 8. Gate

- [x] 8.1 `openspec validate let-a-command-name-the-owner-of-its-protected-fields --strict` — valid.
- [x] 8.2 `./scripts/local-gauntlet.sh` — green: 0 warnings, 24 test projects, DocFX, doc-symbol and
  pack checks. `dotnet test tests/Stratara.Outbox.RabbitMQ.IntegrationTests` — 87 passed;
  `dotnet test tests/Stratara.Orleans.IntegrationTests` — 194 passed (Docker).

## 9. After the review of the implementation

- [x] 9.1 An empty named user is refused like an empty named tenant (`CommandDataOwner.NamedBy`) —
  `CommandDataOwnerTests.ForTransit_CommandNamingAnEmptyUser_ThrowsNamingTheCommandType`,
  `ForRecord_CommandNamingAnEmptyUser_ThrowsNamingTheCommandType`.
- [x] 9.2 The canonical form covers either owner field as soon as one is set —
  `BusEnvelopeCanonicalTests.Of_CommandEnvelope_AUserAddedWithoutATenantProducesDifferentCanonical`.
- [x] 9.3 A command encrypted as a whole is not delivered once its owner is erased, and the
  specification, the guide and `INamesDataOwner` say so —
  `CommandOwnerProtectionTests.TheOwnersErasure_WhileACommandEncryptedAsAWholeWaits_KeepsItFromItsHandler`.
- [x] 9.4 The owner is taken as stated: specification, guide, `INamesDataOwner` remarks, design
  *Risks* — `CommandOwnerProtectionTests.ACommandNamingATenantNobodyHasSeen_CreatesKeyMaterialForIt`.
- [x] 9.5 `ICommandOutboxDispatcher.EnqueueCommandAsync` and `ICommandAuditRepository.AddAsync`
  document the `ArgumentException`; `IntentRecorder`'s summary and the envelope-trust paragraph of
  `SECURITY.md` no longer state the old rule.
- [x] 9.6 One projection from a recorded envelope to what an activation receives
  (`AggregateCommandEnvelope.From`), used by the recorder, the resumer and the integration tests'
  claim helper — `CommandOwnerTests.From_CarriesEverythingAnActivationActsOn`. One fallback for a
  travelling copy in `CommandDataOwner`, shared by the writer and the reader.
- [x] 9.7 The guide's and the interface's examples declare the protected field as `string?`.
