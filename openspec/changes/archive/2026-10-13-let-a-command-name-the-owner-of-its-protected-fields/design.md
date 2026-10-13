## Context

See `proposal.md` — *Why* for the motivation. What shapes the approach is where a command is
serialized today, and with which identities. Every row below was read on `main` at 4.4.4.

| Site | Writes or reads | Tenant | User |
|---|---|---|---|
| `CommandEnvelopeMapper.MapToAsync` (`src/Stratara.Shared/Mediator/Mapping/CommandEnvelopeMapper.cs:40`) — the bus and outbox envelope | writes | `session.TenantId` | `session.ActorUserId` |
| `IntentRecorder.RecordAsync` (`src/Stratara.Orleans/Aggregates/IntentRecorder.cs:31`) — the recorded intent | writes | `session.TenantId` | `session.ActorUserId` |
| `AggregateGrainBehavior.BuildEnvelopeAsync` (`src/Stratara.Orleans/Aggregates/AggregateGrainBehavior.cs:74`) — the forward to a grain | writes | `session.TenantId` | `session.ActorUserId` |
| `CommandAuditRepository.AddAsync` (`src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/Repositories/CommandAuditRepository.cs:51`) — the audit record | writes | `session.TenantId` | `session.UserId` |
| `MediatorCommandWorker.DispatchAsync` (`src/Stratara.Outbox.RabbitMQ/Mediator/MediatorCommandWorker.cs:132`) | reads | `session.TenantId` | `session.ActorUserId` |
| `AggregateGrain.InvokeAsync` (`src/Stratara.Orleans/Aggregates/AggregateGrain.cs:459`) | reads | `session.TenantId` | `session.ActorUserId` |

Facts the decisions rest on:

- **A reader must pass the owner in.** `SecureJsonSerializer` finds the key through the id stored in the
  ciphertext wrapper, but authenticates with `tenantId|userId|scope` supplied by the caller
  (`SecureJsonSerializer.cs:246-266`, `:314-323`). A reader that passes another tenant or user than the
  writer gets a `CryptographicException` from AES-GCM (`AesGcmEncryptionFactory.cs:49`); nothing catches
  it. A reader whose key is gone gets `null` and the field reads as absent (`:249-253`).
- **A key is named by level, tenant and user together** (`SecureJsonSerializer.cs:164`), for every level.
  The user dimension therefore decides the key even for a tenant-scoped field.
- **An ordinary signed-in session has `UserId = null` and `ActorUserId = the user`**
  (`SessionContextMiddleware.cs:56-65`). So today the travelling copy of a protected field is under
  `…:T:U` and the recorded copy under `…:T:` — for every command, not only for on-behalf flows.
- **The envelope is stored as JSON.** The outbox row and the Orleans intent row both hold
  `JsonSerializer.Serialize(envelope)` in one column (`OutboxEntryMapper.cs:20`,
  `CommandIntentStore.cs:42`). The command log already has `TenantId`, `UserId`, `ActorTenantId` and
  `ActorUserId` (`CommandAuditRepository.cs:54-57`). Nothing stamps `TenantId` on write; the tenant
  filter applies to queries only (`ModelBuilderExtensions.cs:60-83`).
- **Nothing in the framework reads the audit payload back.** `CommandLogEntry.CommandJson` has one
  writer and no reader in `src/`.
- **The signature's canonical form is built from field values, length-prefixed, and changing it
  invalidates older signatures** (`BusEnvelopeCanonical.cs`, remarks).
- **A saga handles a bundle under the session that wrote the event** (`SagaWorker.cs:177-179`), so a
  session with no tenant is inherited by every command the saga enqueues.

## Goals / Non-Goals

**Goals:**

- A command can state its data owner once, and every site in the table honours it.
- A command that states none is byte-identical on the wire and in storage, and verifies across the
  release in strict mode.
- A host that cannot read the owner fails loudly; it never runs a handler with emptied fields.
- No schema change.

**Non-Goals:**

- Deriving a command's owner — from the aggregate it names, from the event a saga reacts to, or from
  anything else. A command has no stream; only its author knows whose data it carries.
- Changing what a handler receives when the owner's key is gone. The existing rule (*Unreadable
  encrypted fields degrade rather than fail*, `data-encryption`) applies as it stands.
- Changing the session, or how a saga's session is built.
- Aligning the user dimension of a command that names no owner (D6).

## Decisions

### D1 — The command states the owner through an interface, `INamesDataOwner`

```csharp
namespace Stratara.Abstractions.Security;

public interface INamesDataOwner
{
    Guid DataOwnerTenantId { get; }
    Guid? DataOwnerUserId => null;
}
```

A consumer implements it explicitly from fields the command already has, as `TenantCreated` implements
`IAggregateCreationEvent`, so nothing is added to the command's JSON:

```csharp
public sealed record TrackPageCommand(Guid TenantId, [property: EncryptData(DataSensitivityLevel.TenantScoped)] string? Address)
    : ICommand, INamesDataOwner
{
    Guid INamesDataOwner.DataOwnerTenantId => TenantId;
}
```

The member names carry the `DataOwner` prefix so that a command which also implements
`ITenantScopedRequest` (`Guid TenantId`) can implement both without a clash, and so that the two are
not mistaken for one statement: `ITenantScopedRequest` is compared against the session by the isolation
guard; `INamesDataOwner` is never compared against anything.

*Alternatives considered.*

- **A parameter on the dispatch call** (`EnqueueCommandAsync(command, owner)`), mirroring
  `AppendOnBehalfOfAsync`. Rejected: the audit stage sees only the command, so the recorded copy would
  need a second channel, and a command resumed, republished or forwarded has no call site left to carry
  the argument. The owner is a property of the command's data and belongs on the command.
- **Reusing `ITenantScopedRequest.TenantId`.** Rejected: it would subject every owner-naming command to
  the isolation guard, which refuses exactly the case this change is for — a session whose tenant is not
  the owner's.
- **Taking the owner from the session after promoting it** — the endpoint sets the session's data-owner
  tenant before dispatch. This is the consumer-side route and stays available; it is rejected as the
  framework's answer because the session's data-owner tenant is also what `ITenantScopedRequest` and
  query filters trust, so an anonymous caller would be handed a tenant's session in order to encrypt
  one field.

*Evidence:* `IAggregateCreationEvent.cs`, `ITenantScopedRequest.cs`, the archived change
`2026-08-30-anchor-event-subject-to-the-stream`.

### D2 — An empty named tenant is refused, with `ArgumentException`

A command that implements `INamesDataOwner` and returns `Guid.Empty` is refused at every writing site
before anything is serialized, with a message naming the command type.

*Alternative considered:* fall back to the session, as `IAggregateCreationEvent` does for an empty
tenant. Rejected: that reproduces the finding. The fallback yields valid ciphertext under the acting
session's tenant, and the mistake in the caller — a field that was never filled — becomes invisible.
The precedent that fits is the explicit one: an explicit event subject naming no tenant throws
(`2026-08-28-reject-an-ownerless-event-subject`, shipped in 3.4.0).

The same holds for the user: a command that implements `DataOwnerUserId` and returns `Guid.Empty`
is refused. Both copies would otherwise be sealed under a key naming the tenant and nobody, shared by
every such command in the tenant and reached by no user's erasure, because an erasure refuses an empty
id. *Added after the review of the implementation (2026-10-13).*

### D3 — One resolution, used by every site

A static `CommandDataOwner` in `Stratara.Shared` answers, for a command and a session, the pair
`(Guid TenantId, Guid? UserId)` a copy is written for, and whether the command named it:

- named → `(DataOwnerTenantId, DataOwnerUserId)`, for every copy;
- not named, travelling copy → `(session.TenantId, session.ActorUserId)` — today's rule;
- not named, recorded copy → `(session.TenantId, session.UserId)` — today's rule.

And for a reader: the envelope's owner when it carries one, else `(session.TenantId,
session.ActorUserId)`.

It is public because `Stratara.EventSourcing.EntityFrameworkCore` and `Stratara.Orleans` call it, as
`CommandEnvelopeMapper` in the same package is. Four writers and two readers each computing the pair
inline is how the travelling and the recorded copy came to disagree in the first place.

### D4 — The owner travels as two optional properties on `CommandEnvelope`

```csharp
[JsonPropertyName("DataOwnerTenantId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public Guid? DataOwnerTenantId { get; init; }

[JsonPropertyName("DataOwnerUserId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public Guid? DataOwnerUserId { get; init; }
```

Init-only properties, not constructor parameters: the positional constructor and `Deconstruct` keep
their signatures, so the change is additive for any consumer that constructs or deconstructs an
envelope. Omitted when null: an envelope that names no owner serializes to the bytes it serializes to
today, in the outbox row, the intent row and on the bus.

`AggregateCommandEnvelope` (internal, Orleans-serialized) gains the same two as `[Id(4)]` and `[Id(5)]`
with `null` defaults. Its `ClaimedAt` (`[Id(3)]`) was added the same way and documents that a hand-over
from a silo that does not set it carries none.

*Alternatives considered.*

- **Put the owner into the session context.** Rejected by the brief: the session stays what it is.
- **Let the reader find the owner in the command's JSON** before decrypting. Rejected: with an explicit
  interface implementation the owner is not in the JSON at all, and reading it from the deserialized
  command is circular — deserializing is what needs the owner.
- **Let the reader take tenant and user from the key id in the ciphertext wrapper.** Rejected: a key id
  is opaque to `IKeyStore`'s contract, and authenticating a ciphertext against identities read out of
  that same ciphertext authenticates nothing.

*Evidence:* `CommandEnvelope.cs` (how `Heavy` was added, and that it was added as a parameter),
`AggregateCommandEnvelope.cs`, `OutboxEntryMapper.cs:20`, `CommandIntentStore.cs:42`.

### D5 — The canonical form appends the owner only when there is one

`BusEnvelopeCanonical.Of(CommandEnvelope)` appends, after the payload digest, the owner tenant and then
the owner user (empty when null) — both, whenever `DataOwnerTenantId` is set, and neither otherwise.

The projection stays injective. Every field is length-prefixed, so a five-field and a seven-field
sequence cannot produce the same string: removing the owner from a signed envelope, adding one to an
envelope signed without, or moving characters between the digest and the owner all change the
projection.

Appending conditionally is what keeps a command that names no owner verifiable across the release in
both directions. The alternative — appending two empty fields always — is simpler to state and
invalidates every signature in a mixed fleet, forcing a pass through permissive mode for a change no
ordinary command takes part in.

Both owner fields are appended as soon as *either* is set, not only when the tenant is. A reader
ignores a user without a tenant, so appending on the tenant alone would have been harmless today and
would have left one field of the envelope outside the signature — against the class's own statement
that every field except the signature is covered. *Changed after the review of the implementation
(2026-10-13).*

On the Orleans path the resumed hand-over is built from the signed envelope in the intent record
(`OrleansCommandDispatcher.cs:268`, verified at `:327`), so the owner it passes on is covered by the
same verification as the type name and the session.

### D6 — The user dimension is aligned where an owner is named, and left alone where none is

The proposal's third bullet under *Why* has two possible answers.

**Chosen:** a command that names an owner has every copy written for `(DataOwnerTenantId,
DataOwnerUserId)`. A command that names none keeps both of today's rules, and the specification now
states them.

**Rejected for this change:** switch the travelling copy of every command to `(session.TenantId,
session.UserId)`, the data-owner pair the convention calls for and the recorded copy already uses.

The reason is the third fact under *Context*. For an ordinary signed-in request the two pairs differ,
so the switch re-keys every protected command field in transit, not a handful of on-behalf flows. A
host on the release before reads with the acting user and throws on each of them; a host on the release
after throws on each command still waiting in an outbox or an intent store from before. Both fail
loudly rather than wrongly, but the cost is a required upgrade order and a drained backlog for every
consumer with one protected command field, to align a copy that is transient by design. The durable
copy — the record — is already written for the data-owner pair.

What this change does instead is put the switch within reach. A reader on this release honours an owner
carried by the envelope, so a later major version can have the writer state the data-owner pair on every
envelope and change nothing on the reading side. That step belongs with other wire-format changes of a
major version and is not planned here.

*Evidence:* `SessionContextMiddleware.cs:56-65`, `SecureJsonSerializer.cs:164` and `:246-266`.

> **For the owner's review.** The scope the owner agreed to before this change was drafted was "align
> the user dimension in the same change". Reading the session middleware afterwards showed what that
> costs on the default path, and the artifacts take the narrower reading above. Choosing the wider one
> changes the `mediator-dispatch` and `outbox-and-messaging` deltas (the *names no owner* scenarios),
> the migration plan and task group 3 — revise through `/opsx:update` before approval.

### D7 — The audit record is attributed to the named owner

`CommandLogEntry.TenantId` and `.UserId` take the named owner; `ActorTenantId` and `ActorUserId` stay
the session's. The repository's own remarks already say the row "is tagged by Subject (data owner)", and
it is the only choice that leaves a record decryptable from its own columns: a reader of the command
log passes the row's tenant and user to the serializer, exactly as a reader of an event entry does.

Consequence: a tenant's filtered view of the command log gains commands issued by others that carried
its data, with the issuer in the actor columns. That is the attribution an event appended on another's
behalf already has.

### D8 — A waiting command whose owner is erased is delivered, with the fields absent

No new mechanism. The key is gone, the serializer yields `null` for the field
(`SecureJsonSerializer.cs:249-253`), the handler runs. The same already holds today for a command whose
session tenant is erased while it waits; naming an owner only changes which erasure it is.

*Alternative considered:* have the worker drop such a command. Rejected: the worker cannot tell an
erased key from a field that was absent when the command was written, and dropping a command is a
decision about the consumer's domain. The documentation tells a handler author that a command which
names an owner can arrive after that owner is gone.

## Risks / Trade-offs

- **[A host on an earlier release receives an owner-naming command]** → It fails rather than misreads:
  in strict mode the signature does not verify; otherwise AES-GCM rejects the read. The message is
  retried and kept (*A message a handler cannot take is retried a bounded number of times and then
  kept*), and is deliverable once the host is upgraded. Mitigated by the upgrade order in *Migration
  Plan* and stated in the guide. A command with no protected field at all is read correctly by an older
  host outside strict mode — the owner has nothing to decide there.
- **[Naming an owner is mistaken for authorization]** → It checks nothing. A caller in one tenant can
  dispatch a command naming another: the fields are then encrypted under the other tenant's key and,
  with auditing registered, a record attributed to that tenant exists with the caller as actor — before
  the handler has refused anything, if the audit stage is registered ahead of authorization. This is the
  position an event appended to another tenant's stream is already in (`tenant-isolation`: "a handler
  which must refuse another tenant's stream checks the owner"). Mitigated by documentation and by the
  *Naming an owner does not authorize* scenario; the stage order is the consumer's.
- **[The owner is whatever the command says]** → Before this change the key a command's fields were
  sealed under always came from the session. Now it can come from the command body, and
  `GetOrCreateCurrentKeyAsync` creates key material for a tenant on first use. A host that copies a
  caller-supplied identifier into the declaration — the anonymous endpoint is exactly where this is
  tempting — lets that caller grow the key store without bound and, with auditing ahead of
  authorization, leave records in a named tenant's command log. The framework cannot check a tenant's
  existence: the tenant directory is optional and its tenants are a consumer's aggregates. Mitigated by
  the specification's *taken as stated* paragraph, the guide and `INamesDataOwner`'s remarks. *Raised by
  the review of the implementation (2026-10-13).*
- **[A command encrypted as a whole and D8]** → D8 holds for marked fields. With `[EncryptData]` on
  the class, a destroyed key leaves nothing to rebuild (`SecureJsonSerializer.cs:180-185` returns
  `null`), and both readers throw on a command that deserializes to nothing, so the message is retried
  and kept. That is the existing behaviour for a session tenant erased while a command waits; the
  specification and the guide now state it instead of promising delivery unconditionally. *Raised by
  the review of the implementation (2026-10-13).*
- **[The declaration is implemented implicitly]** → A public `DataOwnerTenantId` property is then
  serialized into the command's JSON, in the clear. It is an id the envelope carries in the clear
  anyway; the guide shows the explicit form.
- **[An owner is named and the bus mode is `Off`]** → Whoever can publish to the bus can alter the
  owner. The read then fails authentication; it does not decrypt under another tenant. Under `Off`
  such a publisher can already forge the whole message.
- **[A consumer reads the divergence in D6 as a defect left open]** → It is stated in the spec, in the
  changelog and in the guide, with the way out: name the owner.

## Migration Plan

1. **Take the release everywhere.** Nothing changes on the wire or in storage: no command names an
   owner yet, and the canonical form of every envelope is what it was. Strict mode stays strict.
2. **Then declare owners**, in a later deployment — or in the same one, with every host that handles
   commands (command workers, silos) deployed before any host that dispatches them.
3. **Rollback.** Remove the declarations and let commands already dispatched with an owner drain before
   hosts go back to the earlier release; a command left behind is kept after its attempts, not lost.

No data migration. Records written before a command named an owner stay readable from their own
columns, as they are today.

## Open Questions

_None that change the specs or the task list. The version this ships in is the owner's decision at
release._
