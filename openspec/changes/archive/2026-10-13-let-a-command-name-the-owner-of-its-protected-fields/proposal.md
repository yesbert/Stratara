# Let a command name the owner of its protected fields

> **Status:** approved

## Why

A command's fields marked `[EncryptData]` are encrypted for the session that dispatches it — for the
session's tenant, in the copy that travels to a deferred handler and in the copy the command audit
records. An event, by contrast, is encrypted for its stream's recorded owner, whoever appends it. The
session says who is acting; it does not say whose data a command carries, and the two part ways in
exactly the flows the session context was given two identities for.

A consumer reported it on 2026-10-10 (consumer finding F-022). Three shapes, one cause:

- **A session with no tenant cannot protect anything.** An anonymous endpoint records a fact; a saga
  reacts under that fact's session and enqueues a command carrying personal data. Outside development
  the serializer refuses a tenant-scoped field with no tenant, so the consumer's only choices are
  `Confidential` — one system-wide key that no erasure shreds — or leaving the field unmarked. The
  consumer left every command field unmarked, and states its command plane as not reached by erasure.
- **A session with another tenant protects under the wrong one.** An operator's session, or any session
  whose data-owner tenant is not the tenant the command's data belongs to, yields ciphertext that is
  valid, readable and keyed to a tenant that had nothing to do with it. Nothing fails. Erasing the real
  owner leaves the audit record readable.
- **The two copies of one command are not written for the same user.** The travelling copy names the
  acting user, the recorded copy the session's data-owner user — for an ordinary signed-in request
  those are a user and nobody. A consumer cannot say which user's erasure reaches a command's fields
  without knowing which copy it is asking about.

This is the command-side counterpart of what `anchor-event-subject-to-the-stream` settled for events.
A command has no stream to anchor to, so the owner has to be stated by the command.

## What Changes

The consumer-visible effect: a command can state whose data its protected fields are, and the framework
then encrypts them for that owner everywhere it serializes the command — whatever session dispatched
it. A command that states nothing behaves, and is stored and published, exactly as before.

- **New opt-in declaration** `INamesDataOwner` (`Stratara.Abstractions`): `Guid DataOwnerTenantId`
  and an optional `Guid? DataOwnerUserId`. It is to a command what `IAggregateCreationEvent` is to a
  stream's first event.
- **Both copies are written for the named owner, tenant and user alike.** The copy the outbox, the bus
  and the Orleans path carry, and the copy the command audit records. For a command that names an
  owner the two can no longer disagree.
- **The audit record is attributed to the named owner.** Its data-owner columns carry the owner the
  command names; its actor columns carry the session's actor, as today. A tenant's view of the command
  log then holds the commands that carried its data.
- **The owner travels with the command.** `CommandEnvelope` gains `DataOwnerTenantId` and
  `DataOwnerUserId`, present only when the command names an owner. The handling host reads the
  command's protected fields for that owner; the session it runs the handler under is the dispatching
  session, unchanged.
- **The signature covers the owner — and only where there is one.** A signed command cannot be
  presented with another owner or with none. A command that names none has the canonical form it had
  before, so a fleet in strict mode keeps verifying across the upgrade without a pass through
  permissive mode.
- **An empty owner is refused.** A command that implements the declaration and names an empty tenant
  is refused when it is recorded or dispatched, naming the command type — it does not fall back to the
  session, which is the silent outcome this change exists to remove.
- **Erasure is stated for the command plane.** The audit record of a command stays as evidence that it
  was issued; its protected fields die with the owner's keys. A command still waiting when its owner
  is erased reaches its handler with those fields absent, as any record does whose key was destroyed.

Not changed: the session context and everything that reads it — authorization, tenant isolation,
correlation, the session a handler runs under. Naming an owner grants nothing. Not changed either: a
command that names no owner, including which user each of its two copies is written for (see
`design.md`, D6 — aligning that default re-keys every protected command in transit and belongs to a
major version; this change puts the mechanism in place that makes it a one-line switch then).

No schema change and no migration: the envelope is stored as JSON in the outbox and in the intent
store, and the command log already has the columns.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `mediator-dispatch`: gains *A command can name the owner of its protected fields* — the declaration,
  what it does to the recorded copy and its attribution, the refusal of an empty owner, what erasure
  leaves of a record, and that it is not an authorization statement.
- `outbox-and-messaging`: gains *A deferred command carries the owner it names* — the owner travels
  beside the session through durable storage and the bus, the handling host reads for it, a command
  naming none is stored and published unchanged, and what an older host does with one that names one.
- `bus-envelope-integrity`: *A message's identity claims are what get signed* gains that the canonical
  form covers a named owner, and that the form of a command naming none is unchanged across the
  release.
- `orleans-execution`: gains *A command's named owner holds on the execution model's path* — recorded,
  forwarded and resumed commands alike.

## Impact

- `src/Stratara.Abstractions/Security/INamesDataOwner.cs` — new.
- `src/Stratara.Contracts/Messages/CommandEnvelope.cs` — two init-only properties, omitted from the
  JSON when unset. The constructor and `Deconstruct` are untouched.
- `src/Stratara.Abstractions/Messaging/BusEnvelopeCanonical.cs` — the owner, appended when present.
- `src/Stratara.Shared/Security/CommandDataOwner.cs` — new and public: the one place that resolves
  which owner a command's fields are written and read for.
- `src/Stratara.Shared/Mediator/Mapping/CommandEnvelopeMapper.cs` — writes through it and stamps the
  envelope.
- `src/Stratara.Outbox.RabbitMQ/Mediator/MediatorCommandWorker.cs` — reads for the envelope's owner.
- `src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/Repositories/CommandAuditRepository.cs`
  — encrypts for and attributes to the named owner.
- `src/Stratara.Orleans/Aggregates/` — `AggregateCommandEnvelope` (two new field ids),
  `IntentRecorder`, `AggregateGrainBehavior`, `OrleansCommandDispatcher` (the resumed hand-over),
  `AggregateGrain` (the read in the activation).
- XML documentation that stated the old rule: `CommandEnvelopeMapper`, `CommandAuditRepository`,
  `ISubjectEraser` (*what it does not cover*), `BusEnvelopeIntegrityOptions`, `BusEnvelopeCanonical`.
- `SECURITY.md` (what the signature covers), `docs/concepts/tenant-aware-encryption.md`,
  `docs/guides/write-a-command-handler.md`, `docs/guides/encrypt-data-setup.md`,
  `docs/guides/hmac-bus-envelope.md`, `docs/concepts/session-context.md`, `llms.txt`, `CHANGELOG.md`.
- `tests/Stratara.WriteStore.Tests` gains project references to `Stratara.Infrastructure` and
  `Stratara.Testing`, so the audit repository can be tested with the real serializer and key store.
- Additive and opt-in. New public API (`INamesDataOwner`, `CommandDataOwner`, two properties on
  `CommandEnvelope`), no package, dependency or tier changes, no schema change.
- Dissolved or superseded legacy sources: none.
- Rolling upgrade: hosts that handle commands take this release before any host dispatches a command
  that names an owner (`design.md`, *Migration Plan*).
