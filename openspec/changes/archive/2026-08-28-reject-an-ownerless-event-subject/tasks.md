# Tasks — Reject an ownerless event subject

Test-first throughout: each implementation task has a test task before it that fails against the
current code for the stated reason.

Note for whoever starts: subject resolution has **no test coverage today** — no test in the repo
mentions `AppendOnBehalfOfAsync`, `EventSubject` or subject resolution. Group 1 therefore builds the
first coverage of the priority walk, not just of the defect.

## 1. Cover the explicit subject

- [x] 1.1 In `tests/Stratara.Infrastructure.Tests/EventSourcing/EventSourceTests.cs`, add a test that
  `AppendOnBehalfOfAsync` with a subject naming a tenant records that tenant on the buffered entry
  even when the session names a different one. This must pass before and after — it pins the
  behaviour the guard has to leave alone.
- [x] 1.2 Add a test that `AppendOnBehalfOfAsync` with `new EventSubject(Guid.Empty)` throws
  `ArgumentException`, that the message names the event type and the stream id, and that no entry is
  buffered (a following `SaveChangesAsync` persists nothing). Fails today: the empty subject is
  accepted and written.
- [x] 1.3 Add a test that the empty explicit subject throws even when the session *does* name a
  tenant — proving the append fails rather than falling through to the session. Fails today for the
  same reason.
- [x] 1.4 Run `dotnet test tests/Stratara.Infrastructure.Tests` and confirm 1.2 and 1.3 fail for the
  stated reason, not on a setup error.

## 2. Guard the explicit subject

- [x] 2.1 In `AppendOnBehalfOfAsync` (`src/Stratara.Infrastructure/EventSourcing/EventSource.cs`),
  reject a subject whose `TenantId` is `Guid.Empty` with `ArgumentException` naming `nameof(subject)`,
  the event type and the stream id — before the override is registered in
  `_explicitSubjectOverrides` and before `AppendAsync` is called.
- [x] 2.2 Update the XML docs on `IEventSource.AppendOnBehalfOfAsync`
  (`src/Stratara.Abstractions/Abstractions/EventSourcing/IEventSource.cs`) and on the implementation
  to state that the subject must name a tenant, with an `<exception>` tag for the new throw.
- [x] 2.3 Update the priority-order comment above `ResolveSubjectAsync` so stage 1 records that the
  entry point has already rejected an empty subject — the reason there is no check there.
- [x] 2.4 `dotnet test tests/Stratara.Infrastructure.Tests` green.

## 3. Cover tenant creation ownership

- [x] 3.1 Add a test in `tests/Stratara.Infrastructure.Tests/EventSourcing/` that creating a stream
  with a `TenantCreated` event, from a session whose data-owner tenant is a *different* tenant,
  records the created tenant's own id as the entry's subject. Fails today: resolution reaches the
  session and records the session's tenant.
- [x] 3.2 Add a test in `tests/Stratara.Shared.Tests/Domain/TenantAggregateTests.cs` that
  `TenantCreated` satisfies the creation-event contract and reports the created tenant's id as its
  owning tenant. Fails today: the event does not implement the contract.
- [x] 3.3 Add a test that a `TenantCreated` round-trips through the framework's serializer to the
  same JSON as before the change — property names, order and absence of any owning-tenant field —
  so the "no migration" claim in `design.md` is checked rather than asserted. Passes today and must
  keep passing.

## 4. Declare the tenant's creation event

- [x] 4.1 In `src/Stratara.Domain/TenantEvents.cs`, make `TenantCreated` implement
  `IAggregateCreationEvent` with an explicit `Guid IAggregateCreationEvent.TenantId => Id;`. Confirm
  `Stratara.Domain.csproj` needs no new package reference.
- [x] 4.2 Update the XML doc on `TenantCreated` to say the created tenant owns the event.
- [x] 4.3 `dotnet test tests/Stratara.Shared.Tests tests/Stratara.Infrastructure.Tests
  tests/Stratara.Projections.Tests` green — the projection tests cover `TenantCreated` and must be
  unaffected.

## 5. Close out

- [x] 5.1 `openspec validate reject-an-ownerless-event-subject --strict` clean.
- [x] 5.2 `./scripts/local-gauntlet.sh` green.
- [x] 5.3 CHANGELOG entry under the next version: both behaviours, both marked breaking, in
  consumer-visible terms. No reference to the internal brief.
