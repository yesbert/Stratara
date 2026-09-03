## 1. The helper says which

- [x] 1.1 Add `BusEnvelopeIntegrityFailure` (`None`, `Absent`, `Invalid`) beside
      `BusEnvelopeIntegrityResult` in `src/Stratara.Abstractions/Messaging/`, documented.
- [x] 1.2 Add the `Verify(..., out BusEnvelopeIntegrityFailure failure)` overload to
      `BusEnvelopeIntegrityVerifier`; a null or empty signature yields `Absent` without consulting the
      signer, a refused one yields `Invalid`, `Skipped` and `Verified` yield `None`. The existing
      overload delegates and discards the reason. Correct its XML doc, which says "mismatches" for
      the absent case too.
- [x] 1.3 Narrow the XML doc on `BusEnvelopeIntegrityResult.RejectedPermissive` and `RejectedStrict`
      from "mismatched" to "did not verify", pointing at the overload for the reason.
- [x] 1.4 Tests in `tests/Stratara.Shared.Tests/Messaging/BusEnvelopeIntegrityVerifierTests.cs`:
      absent under permissive and strict → `Absent`, signer not called; present and refused →
      `Invalid`; verified → `None`; off or no signer → `None` with `Skipped`. Rewrite
      `Verify_NullSignature_StrictMode_StillCallsSignerForLengthCheck` to assert the signer is not
      called and the result is unchanged.

## 2. Two events per worker

- [x] 2.1 `src/Stratara.Diagnostics/LogEvents.cs`: add `CommandProcessing.CommandEnvelopeUnsignedWarning = 105_004`,
      `CommandEnvelopeUnsignedRejected = 105_105`, `EventBundleIntegrity.UnsignedWarning = 111_004`,
      `UnsignedRejected = 111_105`. Narrow the summaries of the four existing ids to the
      present-but-invalid case.
- [x] 2.2 `LoggerCommandExtensions.cs`: add `LogCommandEnvelopeUnsignedWarning` and
      `LogCommandEnvelopeUnsignedRejected`; sharpen the two existing templates so they name a present
      signature that did not verify.
- [x] 2.3 `LoggerEventBundleExtensions.cs`: the same four for event bundles.
- [x] 2.4 `MediatorCommandWorker.VerifyEnvelopeIntegrity`: switch to the overload; choose the event by
      reason; the strict-mode exception message names the case — an unsigned message points at a
      publisher that does not sign, an invalid one at the shared key or tampering.
- [x] 2.5 `ProjectionWorker.VerifyEnvelopeIntegrity` and `SagaWorker.VerifyEnvelopeIntegrity`: the
      same.

## 3. Tests on every consuming path

- [x] 3.1 `tests/Stratara.Outbox.RabbitMQ.Tests/Mediator/MediatorCommandWorkerTests.cs`: split
      `DispatchAsync_PermissiveMode_MissingSignature_DispatchesAnyway` into an unsigned case asserting
      `105_004` and an invalid-signature case asserting `105_003`; assert `105_105` on
      `DispatchAsync_StrictMode_MissingSignature_Throws` and `105_104` on
      `DispatchAsync_StrictMode_TamperedSessionContext_Throws`, and that each exception message names
      its case.
- [x] 3.2 `tests/Stratara.Projections.Tests/Services/ProjectionWorkerIntegrityTests.cs`: the same
      four assertions with `111_004`, `111_003`, `111_105`, `111_104`.
- [x] 3.3 `tests/Stratara.Sagas.Tests/Services/SagaWorkerIntegrityTests.cs`: the same.
- [x] 3.4 One test that pins the *operator alerts on one case only* scenario: a permissive worker
      handed an unsigned bundle emits no event with the invalid-signature id.

## 4. Documentation and changelog

- [x] 4.1 `docs/guides/hmac-bus-envelope.md`, rollout section: during a roll expect `105_004` /
      `111_004` from consumers ahead of publishers; `105_003` / `111_003` mean a key mismatch or
      tampering and should not appear during a roll. Correct the mode table while there: permissive
      accepts an invalid signature too, with a record, not only an unsigned one.
- [x] 4.2 `CHANGELOG.md` `[Unreleased]`: the distinction, the four new ids, and in the first sentence
      that the existing ids now fire for a present-but-invalid signature only.
- [x] 4.3 `./scripts/local-gauntlet.sh` green; `openspec validate --strict` clean.
