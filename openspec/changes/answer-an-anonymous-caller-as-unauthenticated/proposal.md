# answer-an-anonymous-caller-as-unauthenticated

> **Status:** proposed

## Why

The session middleware sets no session for an unauthenticated request, as the `session-context`
capability requires. What happens next depends on the endpoint, and none of it is right:

- **An unguarded command or save answers 500.** The dispatcher, the event source, the command audit and
  the execution model's dispatcher throw a plain `InvalidOperationException("Session context is not set")`
  (`CommandOutboxDispatcher.cs:55`, `EventSource.cs:206,224`, `CommandAuditRepository.cs:34-38`,
  `OrleansCommandDispatcher.cs:47`, `AggregateGrainBehavior.cs:47`). The opt-in problem-details handler
  maps only validation and authorization failures, so this becomes a server error that reads like a
  framework bug.
- **A guarded one answers 403.** A caller without an identity is told it lacks a permission.

The documented workaround is `RequireAuthorization()` on every endpoint. The owner decided
(2026-09-18) that an anonymous caller is answered 401 in both cases.

## What Changes

- **A missing identity has its own failure type.** `SessionRequiredException` in
  `Stratara.Abstractions.Session`, deriving from `InvalidOperationException` so every existing
  `catch (InvalidOperationException)` keeps working, with the message text unchanged. Every site above
  throws it.
- **The opt-in boundary mapping answers an anonymous caller 401.** For `SessionRequiredException`,
  `AuthorizationException` and `TenantAccessDeniedException` alike, when the request's user is not
  authenticated. Where the host registered an authentication scheme the mapping challenges through it —
  so a bearer client gets `WWW-Authenticate`, as `[Authorize]` would give it — and writes the problem
  body where the challenge leaves a 401; without a scheme it writes a 401 problem response.
- **An authenticated caller is unchanged.** Denials stay 403; a `SessionRequiredException` for an
  authenticated user is not converted, because it means the host did not run the session middleware.
- **Hosts that do not opt in see nothing new** except the exception type.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `host-composition`: *Framework failures can be answered as a standard HTTP problem response* — an
  unauthenticated caller is answered as unauthenticated.
- `authorization`: *A denial reaches an HTTP caller as 403* — an anonymous caller's denial is 401.
- `tenant-isolation`: *A rejected request reaches the caller as a forbidden response* — the same for
  the framework's own mapping.
- `event-sourcing-store`: *A successful save publishes what was written* — the no-session failure
  identifies itself as a missing identity.
- `outbox-and-messaging`: *A message carries its originating session and may be signed* — the same for
  dispatch.

## Impact

- Affected code: `src/Stratara.Abstractions/Abstractions/Session/SessionRequiredException.cs` (new),
  the six throwing sites above, `src/Stratara.ServiceDefaults.AspNetCore/StrataraProblemDetailsExceptionHandler.cs`
- Tests: `tests/Stratara.ServiceDefaults.AspNetCore.Tests` (401 with and without a scheme, 403 for an
  authenticated caller, pass-through for an authenticated caller without session), and each throwing site's tests
- Affected docs: `docs/concepts/session-context.md`, the authorization page that states the 403
  mapping, `CHANGELOG.md`
- Public API: additive — one exception type.
- **Behaviour change for hosts that opted into the mapping:** an anonymous caller that received 403
  receives 401. Named in the CHANGELOG under *Changed*.
- Schema: none. Versioning: part of 4.2.0.
