# Design

## Context

See `proposal.md` for why. There is nothing to implement: this document records how each `[Unreleased]`
entry was judged against the specifications as they stand on `main` at `9f8c561`, and what the record leaves
out on purpose.

The entries of `encrypt-recorded-field-changes` and `require-active-membership-for-user-keys` carry their own
deltas and are not judged here. `republish-stored-messages-once-per-pass` is not on `main` and has no
`[Unreleased]` entry yet.

## Goals / Non-Goals

**Goals:**

- Every `[Unreleased]` entry is either stated by a requirement or has a delta.
- Every delta states what the code does today, including where that is narrower than a reader would assume.

**Non-Goals:**

- Changing behaviour. Where the record found behaviour that looks wrong, it is listed under *Risks* and is not
  written into a requirement.
- Correcting the pull-request descriptions or the changelog.

## Decisions

### D1. An entry needs a delta where the text does not say it

An entry needs none where the requirement's own words already forbid what the bug did. A scenario that only
mentions the same subject is not enough: *A host appends with the command-audit registration* speaks of one
command and says nothing of a nested one, so nested causation has a delta even though the word "causation"
is in the specification.

| `[Unreleased]` entry | Pull request | Decision | Governing text, and the reason |
|---|---|---|---|
| `DecryptAsync(stream, scope, purpose)` | #245 | Delta | `data-encryption` has no form that names a purpose, and nothing on a default interface method |
| `WriteV2Blobs` | #245 | Delta | No requirement mentions it |
| Recorded field-changed event can be replayed | #242 | None | *A field-changed event is replayed*: the named property is set to the recorded value |
| Change set leaves out what the aggregate cannot take | #242 | Delta | *The types do not match* and *cannot be written* cover part; what "compatible" means, the absent-value exception and the merger do not appear |
| Unedited collection no longer reported as changed | #242 | Delta | *Nothing changed* says "match" and never says how a collection matches |
| Collection of another shape can be applied | #242 | Delta | No requirement relates applying in memory to replaying |
| `TargetEventTypeName` documentation | #242 | None | Documentation only; *A chain loops* already refuses it |
| Hash chain ends before an unsettled entry | #243 | None | *Events are chained*: each carries the hash of the entry before it in global sequence; *An unchained event SHALL remain unchained rather than being skipped* |
| Nested command causation | #243 | Delta | See above |
| Snapshot guard sees a base class | #243 | Delta | The requirement says "declares", which an inherited property is not, and a host now fails at start-up |
| `AddStrataraTestingEventStore` keeps a unit of work | #243 | None | `event-sourcing-store`: a unit of work the consumer registers takes precedence over the framework's |
| `MigrateAllDatabasesAsync` disposes | #243 | None | A leak; nothing a consumer relies on changes |
| Command-host examples register auditing | #243 | None | `event-sourcing-store`: no documented example shows a session that cannot append |
| One Service Bus sender per topic | #244 | Delta | Nothing in `outbox-and-messaging` speaks of what publishing holds open |
| Unreadable outbox row | #244 | Delta | *Publishing a stored message fails again* covers a message that was read; one that cannot be read is not mentioned |
| `AddAuthorizingCommandOutboxDispatcher()` either order | #244 | None | *Asynchronous command dispatch is guarded identically* has no condition on order |
| Both telemetry methods | #244 | Delta | *Telemetry export* speaks of an endpoint, not of how often or by which method it is configured |
| 5000 ms default OTLP timeout | #244 | Delta | No requirement states a default |
| Blob ciphertext bound to its whole scope | #245 | Delta | The requirement promised it, but the format, the upgrade path and the previous format's narrower binding are observable and unstated |
| Encrypted setting cannot be read as another | #245 | None | *Encrypted settings are bound to their scope and their name*: it cannot be decrypted as a different setting |
| Setting with a revoked or erased key | #245 | Delta | *A subject's key is destroyed* says unrecoverable, not what a read answers |
| Key revoked by another process stops resolving | #245 | Delta | *One key store serves several processes* covers keys created and subjects marked, not keys revoked or erased |
| Bearer principal carries its subject | #245 | Delta | *A bearer token arrives* says the subject is read under its protocol name, and says nothing of the name identifier |
| `AddStrataraPermissionPolicies()` after `AddAuthorization()` | #245 | Delta | "Every permission … usable" has no condition, yet a host's own provider is kept, where it is not usable |
| Repeating a failed revocation or tenant sweep | #245 | None | *Revocation removes the identity as well as the key*: no authorization path outlives the key; *A tenant is swept* |
| `MembershipClaimsPrincipalFactory` remarks | #245 | None | Documentation only; `tenant-directory` already says no claim for several memberships without a selection |
| Replay applies each entry in a scope of its session | #246 | Delta | *Events from several tenants are replayed* says processed under the session; `orleans-execution` needed its own words for construction-time capture, and `projections` has none |
| Replay fails before it empties anything | #246 | None | *A projection can forget a deleted tenant*: a missing place to keep the record fails the replay before anything has been emptied |
| Tenant read model ignores an older fact | #246 | Delta | *Tenants themselves are event-sourced* covers a missing tenant and a repeated creation, not an old fact |
| Handler timeout stalls its partition | #246 | None | *A failing entry stops its partition*: a genuine failure; a projection throws, the checkpoint stays, it is logged and counted |
| Entry retried from a fresh scope | #246 | Delta | No requirement says what a retry resolves |
| `InMemoryMessageBus` competing delivery | #246 | Delta | *The message bus double is used* speaks only of message types on a topic |
| `ProjectionTester` runs production's overload | #246 | Delta | *The harness dispatches exactly as production does* is about aggregates; no requirement mentions a projection harness |
| JSON attributes on field-encrypted types | #248 | Delta | *Fields are encrypted selectively* says "everything else normally"; names, ignores and converters are unstated |
| Heavy command waits for an unreachable keeper | #248 | None | *Waiting costs a recorded command no attempt*: not for a permit |
| Saga needing a session starts | #248 | Delta | No requirement mentions the timer start-up check |
| `IntentGrace` longer than three times `CompletionWindow` | #248 | Delta | *A host configures an invalid setting* does not name the bound |

Evidence: the specification text of `9f8c561` and the diff of each pull request, read in full.

### D2. ADDED where another change touches the requirement

`encrypt-recorded-field-changes` modifies *A field-changed event names the field and carries its new value*,
and `republish-stored-messages-once-per-pass` modifies *A worker drains durable storage in batches under a
distributed lock*. A second MODIFIED block on either would overwrite the other at archive, so in-memory
application and the unreadable row are new requirements. In `orleans-execution` the governing requirements
run to hundreds of lines; ADDED keeps those three additions reviewable.

Rejected: rebasing on those changes, which would tie this record's archive to theirs for no gain in meaning.

### D3. Narrower behaviour is recorded as narrower

The blob requirement promised binding to scope and purpose without qualification. The code binds a stream in
the previous format to tenant and purpose only, and checks a purpose only where the reader names one. Stating
the promise alone would be false for every host that turns on `WriteV2Blobs`, so *A blob is read under a
different scope* now names the current format, and the previous format's scenario is explicit.

Evidence: `src/Stratara.Security/AesGcmSecureBlobEncryptor.cs` (`BuildAad`, `DecryptCoreAsync`) and its tests;
no test reads a previous-format stream under another user of its tenant, which is why task 2.2 asks for the
source location.

### D4. Marked as a backfill in the proposal

No archived change carries a marker for this: the earlier backfill predates this change format, and later
archives refer to it only in prose. The proposal therefore keeps the required status line first and states
beneath it what approval means here, in the terms `openspec/config.yaml` → `rules.proposal` uses.

## Risks / Trade-offs

Each of these is behaviour the record deliberately does not bless. None is in a requirement.

- **A stored setting's key id can be altered to make it read as absent.** The identifier is stored with the value and
  read before anything is authenticated, and a key that does not resolve cannot be told from a destroyed one. Someone
  able to write the row gains nothing by this: deleting the row has the same effect. → Documented in the guide; the
  requirement says "fails authentication", which this case does not reach, and the scenario names ciphertext and tag.
- **A late `TenantCreated` after the deletion re-creates the row.** The changelog says so and #246 leaves it open.
  → Stated in no requirement, neither as a guarantee nor as a limit.
- **A factory-registered stateful process that cannot be built is passed over** by the timer start-up check, so its
  missing timers are not reported at start. → The requirement says it is passed over and says nothing further.
- **A fault of the permit keeper itself still fails an attempt** of a heavy command that never ran its handler
  (`HeavyWorkRunner`, `A_fault_of_the_keeper_itself_is_not_waited_out`). *Waiting costs a recorded command no attempt*
  does not cover it, since it is not waiting. → No delta; noted so that the entry's silence is not read as a promise.
- [The record drifts from the code before it is accepted] → The tasks name the evidence for each delta and are
  checked against `main` at acceptance.
