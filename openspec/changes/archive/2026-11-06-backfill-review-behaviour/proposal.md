# Record the behaviour the review fixes shipped

> **Status:** approved (owner, 2026-11-06)

> **Backfill.** This change records behaviour that is already on `main` and in `[Unreleased]`; nothing
> here is to be built. Approving it says "this is an accurate record", not "build this", so it is
> approved when the record is accepted, necessarily after the behaviour exists.

## Why

Pull requests #242, #243, #244, #245, #246 and #248 merged fixes from a full review of `src/`, and each
says "No spec delta: the code now does what the specs already say". For a good part of them that holds.
For the rest it does not: a consumer of a published package can observe behaviour that no requirement
states, and in a few places a requirement says less than, or something other than, what the code does.
A specification that is the only source of what the framework guarantees has to hold these before they
are released.

## What Changes

No consumer sees anything new: every effect below shipped with those pull requests. What changes is that
the specifications now state it. Four of the effects can stop a host or a test on upgrade, and the
changelog already says so; they are marked **BREAKING**.

- **Encrypted settings.** A setting whose key was revoked or erased reads as absent and the rest of its
  scope still reads; a value that was altered, moved or is not ciphertext fails the read.
- **Blob encryption.** New blobs are authenticated against level, tenant, user and purpose; a reader can
  decrypt against an expected purpose; `WriteV2Blobs` keeps the previous format for a rolling upgrade, and
  the previous format's narrower binding is stated. **BREAKING** for a host on an earlier release reading
  these blobs, as the changelog says.
- **Shared key file.** A key another process revoked or erased stops resolving at the next lookup.
- **Field-encrypted types.** They follow their `System.Text.Json` attributes.
- **Change tracking.** Collections are compared by content, a change set leaves out an absent value for a
  property that cannot be absent, a merge keeps what its payload can hold, and applying a field change
  gives the same result in memory and on replay.
- **Causation.** A nested command hands the session's causation back.
- **Snapshot shape guard.** It covers inherited properties. **BREAKING** for a host whose aggregate inherits
  such a property: it now fails at start-up.
- **Outbox.** An unreadable stored message no longer stops the drain, and the Azure Service Bus transport
  holds one sender per topic.
- **Telemetry.** Both configuration methods compose, and a default export timeout applies.
- **HTTP identity.** A bearer principal carries its subject as the name identifier, and permission policies
  work after `AddAuthorization()`.
- **Tenant read model.** A fact older than the row is skipped.
- **Replay.** Each entry's session is in place before what applies it is resolved.
- **Test support.** `InMemoryMessageBus` delivers to one handler per subscription, **BREAKING** for a test
  that relied on every handler of one subscription seeing every message; `ProjectionTester` runs the handler
  production runs.
- **Execution model.** `IntentGrace` must be longer than three times `CompletionWindow`, **BREAKING** for a
  host configured otherwise, which now fails at start; a retried entry starts from a fresh scope; and the
  timer start-up check builds no saga registered as a type.

`design.md` goes through the `[Unreleased]` section entry by entry, including every entry that needs no
delta and why.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `update-change-tracking`: *Only properties present on both sides participate* and *Absent values are
  compared, not skipped* say what they leave out; collections, merged payloads and in-memory application
  gain requirements.
- `data-encryption`: *Blob ciphertext is bound to the scope and purpose it was written for* and *One key
  store serves several processes over shared storage* are extended; field-encrypted types gain a
  requirement.
- `scoped-settings`: an encrypted setting whose key is gone gains a requirement.
- `mediator-dispatch`: *A command may be recorded before its handler runs* states nested causation.
- `aggregate-rehydration`: *An aggregate's state must be restorable from its serialized form* covers
  inherited properties.
- `outbox-and-messaging`: an unreadable stored message and publishing without accumulating connections gain
  requirements.
- `observability`: *Telemetry export is configured by environment, not by code* states composition and the
  default timeout.
- `authorization`: *Catalog permissions are usable as HTTP authorization policies* states registration order
  and a host's own provider.
- `external-identity`: *The provider's subject remains the identifier after claim mapping* states the name
  identifier.
- `tenant-directory`: *Tenants themselves are event-sourced* states that an older fact is skipped.
- `projections`: *A replayed event is applied under the session that produced it* states the order of session
  and resolution.
- `test-support`: *In-memory doubles honour the contracts they stand in for* gains competing delivery;
  `ProjectionTester` gains a requirement.
- `orleans-execution`: the grace bound, the retry scope and the timer start-up check gain requirements.

## Impact

- The claim "No spec delta" in the descriptions of #242, #243, #244, #245, #246 and #248 is superseded for the
  entries this change records. Those descriptions are not edited.
- `openspec/specs/` for the thirteen capabilities above, through the deltas, at archive.
- No file under `src/`, `tests/` or `docs/` changes, and neither does `CHANGELOG.md`. Versioning: none — a
  specification-only change does not bump.
- Sequence with the open changes: `encrypt-recorded-field-changes` also touches `update-change-tracking` and
  `data-encryption`, and `republish-stored-messages-once-per-pass`, not yet on `main`, touches
  `outbox-and-messaging`. The deltas here do not modify a requirement either of them modifies, so they can
  archive in any order.
- Legacy sources dissolved: none.
