> **Status:** approved

# Say whether a signature was absent or wrong

## Why

Permissive mode exists for one procedure: rolling a fleet over to signed messages, host by host.
During that window every publisher that has not yet restarted emits unsigned envelopes, and every
consumer that has already restarted records one failure per envelope. That is the expected, benign
case. It is reported with the same words, the same event id and the same level as a signature that
is present and does not verify — the case that means a publisher holds the wrong key, or somebody is
publishing envelopes they cannot sign.

An operator reading the log during the one procedure the mode was built for therefore sees
*"integrity verification failed — signature mismatch"* and cannot tell a rolling restart from an
attack. In a consumer's fleet on 2026-09-03 it took correlating container start times across three
hosts to establish that a warning was the former. The log alone could not say.

The conflation begins in the capability itself: the scenario titled *A signature is missing in
permissive mode* is written for "an unsigned or invalid message", and the verification helper passes
an absent signature straight to the signer, which reports it as a non-match. Nothing downstream can
recover the distinction, because nothing upstream ever made it.

## What Changes

- **Verification reports whether the signature was absent or invalid.** A consumer of the published
  verification helper can ask which of the two happened, in permissive and strict mode alike. The
  existing result values keep their meaning and their callers keep compiling; the distinction is an
  addition beside them.
- **Every consuming worker records the two cases as two events.** The command worker, the projection
  worker and the saga worker each emit a distinct event id and distinct text for an unsigned message
  and for a message whose signature does not verify. In permissive mode both are warnings: an
  unsigned message is expected during a roll and is, after the roll, exactly the signal that a
  publisher was missed. In strict mode both are errors, and the rejection states which of the two it
  was.
- **The existing event ids narrow rather than change.** The ids that today fire for any failure
  continue to fire for a present-but-invalid signature only, and their text says so. An alert keyed
  on one of them stops firing for unsigned messages; the new ids carry those. This is the one
  consumer-visible consequence beyond the log text, and the changelog names it.
- The documentation for the rollout procedure says which event to expect during a roll and which one
  means something is wrong.

Nothing about what is signed, what is verified, or which messages are dispatched changes. A message
that was delivered before this change is delivered after it; a message that was refused is refused.

## Capabilities

### New Capabilities

_none_

### Modified Capabilities

- `bus-envelope-integrity`: the requirement *Verification has three modes with an explicit rollout
  path* gains the guarantee that a recorded failure says whether the signature was absent or
  invalid, on every consuming path. Its permissive-mode scenario, which today covers "unsigned or
  invalid" as one case, is split into two.

## Impact

- `src/Stratara.Abstractions/Messaging/BusEnvelopeIntegrityVerifier.cs` — gains the distinction;
  the existing entry point is unchanged.
- `src/Stratara.Abstractions/Messaging/BusEnvelopeIntegrityResult.cs` — documentation narrows
  "mismatched" to "did not verify" and points at where the reason is.
- `src/Stratara.Diagnostics/LogEvents.cs` — two new ids under `CommandProcessing`, two under
  `EventBundleIntegrity`; the four existing integrity ids keep their numbers and narrow their
  meaning.
- `src/Stratara.Shared/Diagnostics/Extensions/LoggerCommandExtensions.cs` and
  `LoggerEventBundleExtensions.cs` — two new messages each; the existing four sharpen their text.
- `src/Stratara.Outbox.RabbitMQ/Mediator/MediatorCommandWorker.cs`,
  `src/Stratara.Projections/Services/ProjectionWorker.cs`,
  `src/Stratara.Sagas/Services/SagaWorker.cs` — the verification branch chooses between the two
  events, and the strict-mode rejection message states which case it is.
- `docs/guides/hmac-bus-envelope.md` — the rollout section names the two events.
- `CHANGELOG.md` — `[Unreleased]`, with the narrowed meaning of the existing ids called out.
- Additive on every published surface: a patch release.
- Source: consumer finding F-009, observed 2026-09-03 on a test fleet during a permissive rollout.
