# Tasks

Every task checks that the record is accurate. None changes code, tests or documentation. A task that finds the
record wrong corrects the delta through `/opsx:update`, or, where the behaviour looks wrong rather than the
record, goes to the owner.

## 1. Change tracking

- [ ] 1.1 *Only properties present on both sides participate* and *Absent values are compared, not skipped* match
      the code. Verify: `A_property_that_differs_only_in_nullability_takes_part`,
      `A_sequence_of_another_shape_takes_part_and_is_compared_by_content`,
      `A_property_with_a_private_setter_takes_part` and
      `An_absent_value_for_a_property_that_cannot_be_absent_is_skipped` in
      `tests/Stratara.Shared.Tests/Merging/ChangeSetBuilderTests.cs`.
- [ ] 1.2 *Collections are compared by what they hold* matches the code. Verify: `A_collection_with_the_same_content_is_not_a_change`,
      `A_collection_with_different_content_is_a_change`, `A_set_is_compared_regardless_of_order`,
      `A_dictionary_with_a_different_value_is_a_change` and `A_dictionary_key_only_one_side_recognises_is_a_change` in
      `ChangeSetBuilderTests.cs`; `An_unedited_collection_keeps_the_current_value` in
      `tests/Stratara.Shared.Tests/Merging/ChangeMergerTests.cs`.
- [ ] 1.3 *A merged update holds only what its properties can hold* matches the code. Verify:
      `A_property_that_cannot_hold_the_aggregates_value_is_copied_through` and
      `An_absent_current_value_does_not_replace_a_value_that_cannot_be_absent` in `ChangeMergerTests.cs`.
- [ ] 1.4 *A field change gives the same result in memory and on replay* matches the code. Verify:
      `A_collection_of_another_shape_is_applied_like_a_stored_one` and
      `A_stored_change_to_an_untyped_property_keeps_the_json_value` in
      `tests/Stratara.Shared.Tests/FieldChangedEventExtensionsTests.cs`.

## 2. Encryption and settings

- [ ] 2.1 The blob requirement's current format and purpose scenarios match the code. Verify:
      `A_blob_does_not_decrypt_under_a_scope_it_was_not_written_for`,
      `A_blob_does_not_decrypt_when_the_reader_expects_another_purpose` and
      `A_legacy_blob_without_a_purpose_field_decrypts_whatever_purpose_the_reader_expects` in
      `tests/Stratara.Security.Tests/AesGcmSecureBlobEncryptorTests.cs`; the default interface method in
      `src/Stratara.Abstractions/Abstractions/Security/ISecureBlobEncryptor.cs`.
- [ ] 2.2 The previous format's scenarios match the code. Verify:
      `A_v2_blob_written_for_a_rolling_upgrade_still_starts_with_0x02_and_decrypts_in_a_v3_reader` in
      `AesGcmSecureBlobEncryptorTests.cs`; for a stream read under another user of its tenant, which no test covers,
      `BuildAad` in `src/Stratara.Security/AesGcmSecureBlobEncryptor.cs`; the rollout statement under *Rolling
      upgrades* in `docs/guides/encrypt-data-setup.md`.
- [ ] 2.3 The shared key file scenarios match the code. Verify: `A_key_another_instance_revoked_is_no_longer_resolved_from_memory`
      and `A_scope_another_instance_erased_is_no_longer_resolved_from_memory` in
      `tests/Stratara.Security.Tests/EnvelopeFileKeyStoreTests.cs`.
- [ ] 2.4 *A type with marked fields follows its JSON attributes* matches the code. Verify:
      `SerializeDeserialize_PropertyLevel_ARenamedPropertyRoundTrips`,
      `SerializeDeserialize_PropertyLevel_ARenamedPropertyRoundTripsWhenNothingIsEncrypted`,
      `Serialize_PropertyLevel_StoresPropertiesUnderTheirClrNames`, `Serialize_PropertyLevel_LeavesOutAnIgnoredProperty`,
      `Serialize_PropertyLevel_HonoursAConditionalIgnore`, `Serialize_PropertyLevel_WritesAPropertyThroughItsOwnConverter`,
      `Deserialize_PropertyLevel_APayloadInTheEarlierShape_StillReads` and
      `Deserialize_PropertyLevel_OrdinaryJsonUnderTheContractNames_StillReads` in
      `tests/Stratara.Infrastructure.Tests/Security/SecureJsonSerializerTests.cs`.
- [ ] 2.5 *An encrypted setting whose key is gone reads as absent* matches the code. Verify:
      `A_value_whose_key_was_destroyed_reads_as_absent_and_the_rest_of_the_scope_still_reads`,
      `A_tampered_value_fails_the_read`, `A_settings_ciphertext_copied_to_another_settings_row_does_not_decrypt`,
      `Ciphertext_is_scope_bound`, `A_plaintext_row_of_an_encrypted_setting_fails_the_read` and
      `Cancellation_while_reading_an_encrypted_value_is_not_treated_as_an_unreadable_value` in
      `tests/Stratara.Identity.EntityFrameworkCore.Tests/EncryptingSettingStoreTests.cs`; for the inherited scenario, which
      that class does not exercise, the statement in `docs/guides/scoped-settings.md`.

## 3. Dispatch, aggregates and messaging

- [ ] 3.1 The nested causation scenarios match the code. Verify:
      `A_command_dispatched_inside_another_hands_the_causation_back_when_it_returns`,
      `A_command_with_a_result_dispatched_inside_another_hands_the_causation_back_when_it_fails` and
      `The_outermost_command_leaves_its_own_id_as_the_causation` in
      `tests/Stratara.EventSourcing.Pipeline.CommandAudit.Tests/CommandAuditBehaviorTests.cs`.
- [ ] 3.2 The inherited property scenario matches the code. Verify:
      `An_auto_property_without_a_public_setter_declared_on_a_base_class_is_a_violation` in
      `tests/Stratara.Infrastructure.Tests/EventSourcing/AggregateSnapshotShapeGuardTests.cs`.
- [ ] 3.3 *A stored message that cannot be read does not stop the drain* matches the code. Verify:
      `EnqueueOutboxEntriesAsync_AnEntryThatCannotBeRead_IsLoggedAndKept_AndTheEntriesBehindItArePublished` in both
      `tests/Stratara.Outbox.RabbitMQ.Tests/Outbox/CommandOutboxDispatcherTests.cs` and `EventBundleOutboxDispatcherTests.cs`;
      `ExecuteAsync_CommandDrainThatFails_IsLogged_AndEventBundlesAreStillDrainedInThatPass` in `OutboxWorkerTests.cs`.
- [ ] 3.4 *Publishing does not accumulate connections to the broker* matches the code. Verify:
      `TwoPublishesToOneTopic_UseOneSender`, `EachTopic_GetsItsOwnSender` and `DisposingTheBus_DisposesTheSendersItOpened` in
      `tests/Stratara.Outbox.RabbitMQ.Tests/Messaging/AzureServiceBusPublishTests.cs`.
- [ ] 3.5 The telemetry scenarios match the code. Verify: `Both_methods_with_an_otlp_endpoint_build_the_providers`,
      `Both_methods_in_the_other_order_and_twice_build_the_providers` and
      `Request_instrumentation_is_added_once_when_it_is_configured_twice` in
      `tests/Stratara.ServiceDefaults.AspNetCore.Tests/AspNetCoreOpenTelemetryExtensionsTests.cs`;
      `Without_a_configured_timeout_the_exporter_waits_five_seconds`, `A_configured_timeout_is_kept`,
      `A_timeout_in_the_process_environment_is_kept` and `Configuring_telemetry_leaves_the_process_environment_alone` in
      `tests/Stratara.ServiceDefaults.Tests/OtlpExportTimeoutTests.cs`.

## 4. HTTP identity and the tenant read model

- [ ] 4.1 The permission policy scenarios match the code. Verify: `Permission_policies_resolve_whether_authorization_was_added_before_or_after`,
      `A_fallback_policy_the_host_configures_is_still_served` and `A_policy_provider_the_host_supplies_is_kept` in
      `tests/Stratara.Identity.AspNetCore.Tests/PermissionPolicyTests.cs`.
- [ ] 4.2 The bearer principal scenarios match the code. Verify: `A_validated_bearer_principal_carries_its_subject_as_the_name_identifier`,
      `A_name_identifier_the_token_already_carries_is_kept` and `A_token_validated_handler_the_host_configures_still_runs` in
      `tests/Stratara.Identity.AspNetCore.Tests/OpenIdConnectAuthenticationExtensionsTests.cs`; for the replaced event handling,
      which no test covers, the remarks of `AddStrataraJwtBearer` in
      `src/Stratara.Identity.AspNetCore/DependencyInjection/OpenIdConnectAuthenticationExtensions.cs`.
- [ ] 4.3 The older-fact scenarios match the code. Verify: `A_rename_older_than_the_row_does_not_overwrite_the_newer_name`,
      `A_repeated_deactivation_after_a_later_activation_leaves_the_tenant_active`,
      `A_fact_of_the_version_the_row_already_holds_is_not_written_again`,
      `A_locale_change_older_than_the_row_does_not_overwrite_the_newer_locale` and
      `A_reassignment_older_than_the_row_does_not_overwrite_the_newer_customer` in
      `tests/Stratara.Projections.Tests/Multitenancy/TenantProjectionTests.cs`.

## 5. Replay, test support and the execution model

- [ ] 5.1 The replay scenario matches the code. Verify: `ReplayCallback_ADependencyThatTakesItsTenantWhenConstructed_TakesTheTenantOfEachEntry`
      in `tests/Stratara.Projections.Tests/Services/ProjectionReplayWorkerTests.cs`.
- [ ] 5.2 The message bus double's scenarios match the code. Verify: `Handlers_on_one_subscription_compete_so_each_message_reaches_one_of_them`,
      `Each_subscription_on_a_topic_receives_every_message` and
      `A_competing_handler_for_another_message_type_does_not_take_a_message_it_cannot_handle` in
      `tests/Stratara.Testing.Tests/InMemoryMessageBusTests.cs`.
- [ ] 5.3 *A test invokes a projection's handler as production does* matches the code. Verify:
      `A_projection_declaring_both_overloads_has_its_payload_handler_run_as_in_production` in
      `tests/Stratara.Testing.Tests/ProjectionTesterTests.cs`; for the enveloped-only scenario, the order in
      `src/Stratara.Testing/ProjectionTester.cs`.
- [ ] 5.4 *A command's grace outlasts the collection of its completion* matches the code. Verify:
      `A_completion_window_not_shorter_than_the_renewal_period_fails_the_start` and
      `A_completion_window_shorter_than_the_renewal_period_starts` in
      `tests/Stratara.Orleans.Tests/OrleansOptionsValidatorTests.cs`; the defaults in
      `src/Stratara.Orleans/Aggregates/OrleansCommandDispatcher.cs`.
- [ ] 5.5 *An entry is retried from a fresh scope* matches the code. Verify:
      `A_projection_entry_retried_after_a_missing_preceding_fact_is_applied_from_a_fresh_scope` and
      `A_saga_entry_retried_after_a_missing_preceding_fact_is_applied_from_a_fresh_scope` in
      `tests/Stratara.Testing.Orleans.Tests/PrecedingFactRetryScopeOnTheExecutionModelTests.cs`.
- [ ] 5.6 *The timer start-up check builds no saga to learn its type* matches the code. Verify:
      `A_saga_role_host_whose_sagas_need_a_session_to_be_built_starts`,
      `A_host_without_saga_grains_whose_saga_needs_a_session_to_be_built_starts` and
      `A_process_that_needs_a_session_to_be_built_still_fails_the_start_without_its_timers` in
      `tests/Stratara.Orleans.Tests/TimerPortsTests.cs`; for the factory case, `SagaTypeOf` in
      `src/Stratara.Orleans/Timers/TimerPorts.cs`.

## 6. Close

- [ ] 6.1 Every entry the design rates "None" is stated by the requirement it names. Verify: read each against
      `openspec/specs/` on `main` at acceptance.
- [ ] 6.2 `openspec validate backfill-review-behaviour --strict` passes. Verify: the output.
- [ ] 6.3 The owner has decided the items under *Risks / Trade-offs* in `design.md`, each by a change of its own or by
      leaving it. Verify: the decision is recorded here or in the change that takes it up.

## Workflow follow-up

- The owner sets the status line to `approved` when the record is accepted.
- Archive the change with `/opsx:archive`, in any order relative to `encrypt-recorded-field-changes`,
  `require-active-membership-for-user-keys` and `republish-stored-messages-once-per-pass`.
