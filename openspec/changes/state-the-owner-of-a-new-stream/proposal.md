# State the owner of a new stream

> **Status:** approved

## Why

Since 4.0.0 a stream keeps the owner its first event recorded (`anchor-event-subject-to-the-stream`), which
closed the consumer report NextPA F-005 for every event *after* the first. The first one is still decided
by whoever is acting: when the creation event names no tenant and no subject is stated, the owner is the
session's tenant, silently. That is how the defect F-005 describes arose — an operator creates a customer,
and the customer's whole stream is keyed to the operator's tenant, with nothing failing and nothing logged.
The consumer guards against it with a build-time ratchet of its own; every other consumer has the same
exposure and no guard.

Two smaller gaps from the same report stay open as well. There is `AppendOnBehalfOfAsync` but no
`CreateOnBehalfOfAsync`, so a caller looking for the explicit route at creation finds it only for appends
(an `IAggregateCreationEvent` is the other way, and is not discoverable from the API). The third point of
the report — documenting the resolution order where handler authors meet it — is done
(`docs/guides/write-a-command-handler.md`, the owner chain), and is not part of this change.

## What Changes

The consumer-visible effect: a creator can state a new stream's owner directly, and a host can decide
whether a new stream may take its owner from the session at all.

- **`IEventSource.CreateOnBehalfOfAsync<TAggregate>(streamId, event, subject)`** — creates the stream with
  the stated owner, which every later event on it keeps. Fails like `CreateAsync` when the stream exists,
  and like `AppendOnBehalfOfAsync` when the subject names no tenant. Default implementation in terms of
  `ExistsAsync` and `AppendOnBehalfOfAsync`, so an implementer outside the framework gets correct
  behaviour without change.
- **`EventSourcingOptions.NewStreamOwnerFromSession`** (section `EventSourcing`): `Allow` (default — as
  today), `Warn` (as today, plus a warning `102_104` naming the stream, the event and the session's tenant),
  or `Refuse` (the append fails before anything is staged, naming the stream, the event and the three ways
  to state an owner). It applies only to the first event of a stream that does not exist yet, and only when
  the session is what would decide the owner — a stated subject, a creation event carrying a tenant, and
  every append to an existing stream are untouched.

Not changed: the resolution order, the default behaviour, and appends to existing streams.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `event-sourcing-store`: a new requirement, *A new stream's owner can be stated at creation, and a host
  can require it to be*.

## Impact

- `src/Stratara.Abstractions/Abstractions/EventSourcing/IEventSource.cs` — `CreateOnBehalfOfAsync` with a
  default implementation; the interface remarks on the owner chain mention the policy.
- `src/Stratara.Infrastructure/EventSourcing/EventSource.cs` — `CreateOnBehalfOfAsync`; the session step of
  `ResolveSubjectAsync` learns whether the stream is new and applies the policy.
- `src/Stratara.Shared/EventSourcing/EventSourcingOptions.cs` — `NewStreamOwnerFromSession` and the enum
  `NewStreamOwnerPolicy`.
- `src/Stratara.Diagnostics/LogEvents.cs` — `102_104` (`NewStreamOwnerTakenFromSession`), and the
  event source's source-generated logger method.
- `docs/guides/write-a-command-handler.md` (the owner chain, around line 70), `CHANGELOG.md`.
- Builds on `append-against-the-version-a-handler-read`, which makes `AddEventSourcing()` bind the
  `EventSourcing` section and hands the event source its options. No package, dependency or tier changes.
  Additive: a patch release.
