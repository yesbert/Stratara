# Tasks

## 1. The failure type

- [x] 1.1 `SessionRequiredException` in `src/Stratara.Abstractions/Abstractions/Session/`, XML-documented.
- [x] 1.2 Thrown at `CommandOutboxDispatcher.cs:55`, `EventSource.cs:206,224`, `CommandAuditRepository.cs:34-38`,
      `OrleansCommandDispatcher.cs:47`, `AggregateGrainBehavior.cs:47`; each method's `<exception>` doc updated.
- [x] 1.3 The existing tests of those sites assert the new type.
- [x] 1.4 The two sites without a test get one: `OrleansCommandDispatcher` and `AggregateGrainBehavior` in
      `tests/Stratara.Orleans.Tests/NoSessionDispatchTests.cs`; the second `EventSource` site (the save after the
      session was cleared) in `EventSourceTests.SaveChangesAsync_SessionClearedAfterTheAppend_ThrowsSessionRequiredException_BeforeAnythingIsWritten`.
- [x] 1.5 A sweep of `src/` for every other no-session failure found no further site: the guards' denials
      (`AuthorizingMediator`, `AuthorizingCommandOutboxDispatcher`, `TenantIsolationGuard`) stay denials, the workers'
      and grains' "failed to deserialize / carries no session" failures concern a recorded envelope rather than a
      caller, and the secure serializer's null-tenant failure is about a tenant that may also be passed explicitly.

## 2. The boundary

- [x] 2.1 `StrataraProblemDetailsExceptionHandler`: D2 and D3.
- [x] 2.2 Tests: anonymous + `SessionRequiredException` → 401 problem without a scheme; → challenge with a bearer
      scheme (status 401, `WWW-Authenticate` present, problem body); anonymous + `AuthorizationException` and
      `TenantAccessDeniedException` → 401; authenticated + denial → 403; authenticated + `SessionRequiredException`
      → not handled.

## 3. Documentation

- [x] 3.1 `docs/concepts/session-context.md`: what an anonymous caller receives, and no more `RequireAuthorization()`
      workaround needed for that.
- [x] 3.2 The authorization page's statement of the 403 mapping.
- [x] 3.3 `CHANGELOG.md` `[Unreleased]` → *Added* (the exception), *Changed* (401 for anonymous callers), *Fixed* (500).
