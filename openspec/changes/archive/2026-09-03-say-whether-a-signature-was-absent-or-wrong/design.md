# Design — Say whether a signature was absent or wrong

## Context

See `proposal.md` → *Why*. What matters here is where the distinction is lost and who reads the
result.

`BusEnvelopeIntegrityVerifier.Verify` (`src/Stratara.Abstractions/Messaging/BusEnvelopeIntegrityVerifier.cs`)
is a public static helper returning `BusEnvelopeIntegrityResult`, an enum of four values: `Skipped`,
`Verified`, `RejectedPermissive`, `RejectedStrict`. It hands the signature, null or not, to
`IBusEnvelopeSigner.Verify`, and the HMAC signer answers `false` for an absent signature before doing
any work (`HmacBusEnvelopeSigner.cs:47`, pinned by the *No signature is presented* scenario). The
result is the same `Rejected*` value either way. The helper's own XML doc describes the two rejection
values as "the signature mismatches", which is already inaccurate for the absent case.

Three workers consume the result with one shape of code, and the shape is the reason the enum is
what it is: return on `Skipped or Verified`, throw on `RejectedStrict`, warn otherwise
(`MediatorCommandWorker.cs:167`, `ProjectionWorker.cs:194`, `SagaWorker.cs:195`). Each has one
warning and one error message. The command worker uses `CommandProcessing.CommandEnvelopeIntegrityWarning`
(`105_003`) and `CommandEnvelopeIntegrityRejected` (`105_104`); the projection and saga workers share
`EventBundleIntegrity.IntegrityWarning` (`111_003`) and `IntegrityRejected` (`111_104`). The
numbering convention in `LogEvents.cs` puts warnings in the low tens and errors at `_1xx`; `105_004`
and `111_004` are free, as are `105_105` and `111_105`.

Logging is source-generated: one `[LoggerMessage]` per event on the partial extension classes in
`Stratara.Shared`. A new event is a new id, a new method, a new message template.

The helper is public and `Stratara.Abstractions` is Tier-A, so a consumer that wrote its own worker
may be calling `Verify` and switching on the enum. Nothing in this repository does, but the surface
is published.

## Goals / Non-Goals

**Goals:**

- The three workers can tell absent from invalid without re-deriving it, and cannot accidentally
  disagree about which is which.
- The existing entry point and the existing enum values keep their meaning for any caller outside
  this repository.
- A consumer's alert on the existing "rejected" ids keeps firing for the case those ids now
  exclusively mean; the unsigned case is reachable by a new id.

**Non-Goals:**

- Changing levels. Both permissive-mode records stay at warning, both strict-mode records at error.
  The proposal says why; the ids are what an alert should key on.
- Distinguishing *why* a present signature does not verify. A wrong key and a tampered payload look
  identical to the verifier by construction, and telling them apart would require a second key.
- Touching the signer. `IBusEnvelopeSigner.Verify(payload, signature)` keeps answering `false` for
  an absent signature; the distinction is made before it is asked.

## Decisions

### The distinction is made in the helper, before the signer is consulted, and surfaced as an addition

`BusEnvelopeIntegrityVerifier` gains an overload of `Verify` with a trailing
`out BusEnvelopeIntegrityFailure failure` parameter, where the new enum has the values `None`,
`Absent` and `Invalid`. The overload checks for a null or empty signature first: an absent signature
never reaches the signer and yields `Absent`; a present one that the signer refuses yields `Invalid`;
`Skipped` and `Verified` yield `None`. The return value is the existing enum with its existing
meaning. The four-parameter `Verify` stays and delegates, discarding the reason.

The workers switch to the overload. Their branch structure does not change: return on `Skipped or
Verified`, throw on `RejectedStrict`, warn otherwise — but inside the throw and the warn branch the
reason selects one of two log events, and the strict-mode exception message says which case it is.

*Rejected: two new enum values, `UnsignedPermissive` and `UnsignedStrict`.* Additive in the
binary-compatibility sense and wrong in the behavioural one: a consumer that copied the workers'
branch shape treats anything that is not `RejectedStrict` as "warn and dispatch". The new strict
value would fall into that branch, and an unsigned message would be dispatched under strict mode by
code that was correct before the upgrade. A patch release must not do that.

*Rejected: a richer return type on a new method.* Cleaner to read than an `out` parameter, but it
duplicates the dispatch decision across two types that have to agree, and every caller then chooses
between two methods that differ only in how much they say. One method that says more, beside one
that says what it always said, is the smaller surface.

*Rejected: make the distinction in each worker by inspecting the envelope's signature field itself.*
Three copies of `string.IsNullOrEmpty` is not much, but three places that have to agree on what
"absent" means — null, empty, whitespace — is exactly how the next inconsistency starts. The helper
exists so the three workers share one set of rules; this is one more rule.

Evidence: `BusEnvelopeIntegrityVerifierTests`, which will pin that an absent signature reports
`Absent` without the signer being called, and that a present one the signer refuses reports
`Invalid`.

### The existing ids narrow; the new ids carry the unsigned case

`105_003` and `111_003` continue to mean "the signature did not verify, dispatched under permissive
mode", and their templates lose the ambiguity: the text names a present signature that did not
verify. `105_104` and `111_104` do the same for strict mode. New `105_004` and `111_004` record an
unsigned message dispatched under permissive mode; new `105_105` and `111_105` record an unsigned
message refused under strict mode.

The alternative — new ids for the *invalid* case and letting the existing ids carry *unsigned* — was
weighed against what an existing alert on `_003` most plausibly wants. It was configured, before this
change, to catch "something is wrong with signing". After it, the same id fires only for the case
that is genuinely wrong, and goes quiet during a roll. That is the better default for an unattended
alert; the roll is watched by a person, who reads the new id.

The strict-mode exception text, which today advises checking the shared key and the bus for tampered
messages, splits along the same line: an unsigned message under strict mode points at a publisher
that does not sign, which is a rollout mistake, not a key mismatch.

Evidence: the three worker integrity test classes, which will assert on the event id each case
records; `LogEvents.cs` for the free numbers.

### Both permissive-mode records stay warnings

The finding suggested the unsigned case might belong at information. It does not: during the roll it
is expected, but once the roll is complete it is the only signal that a publisher was missed, and
that publisher's messages are exactly the ones anyone with publish rights could have minted. A level
that production hosts routinely filter would hide it. The two cases differ in what an operator
should *do*, and that is what distinct ids are for; they do not differ in whether the operator should
be told.

## Risks / Trade-offs

- [A consumer's dashboard counted `111_003` as "unsigned messages seen during the roll"] → After
  upgrading, that count stops moving during a roll and `111_004` moves instead. The changelog entry
  names the narrowing and the new ids in its first sentence, and the rollout guide names the id to
  watch.
- [A consumer's own signer relied on being called with a null signature] → It is not called any
  more, on either overload; the old one delegates to the new. Its result is unchanged, because the
  signer contract already required `false` for an absent signature and the HMAC signer answered it
  before doing any work. The existing test that asserts the signer is called for a null signature
  documented that incidental path, not a guarantee, and is rewritten to assert the opposite.
- [Whitespace-only signature] → Treated as present and invalid, not absent. A publisher that emits
  whitespace is not "not yet signing"; it is broken, and the record should say so.
