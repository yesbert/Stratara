# Tasks

## 1. The failure type

- [ ] 1.1 `SessionRequiredException` in `src/Stratara.Abstractions/Abstractions/Session/`, XML-documented.
- [ ] 1.2 Thrown at `CommandOutboxDispatcher.cs:55`, `EventSource.cs:206,224`, `CommandAuditRepository.cs:34-38`,
      `OrleansCommandDispatcher.cs:47`, `AggregateGrainBehavior.cs:47`; each method's `<exception>` doc updated.
- [ ] 1.3 The existing tests of those sites assert the new type.

## 2. The boundary

- [ ] 2.1 `StrataraProblemDetailsExceptionHandler`: D2 and D3.
- [ ] 2.2 Tests: anonymous + `SessionRequiredException` → 401 problem without a scheme; → challenge with a bearer
      scheme (status 401, `WWW-Authenticate` present, problem body); anonymous + `AuthorizationException` and
      `TenantAccessDeniedException` → 401; authenticated + denial → 403; authenticated + `SessionRequiredException`
      → not handled.

## 3. Documentation

- [ ] 3.1 `docs/concepts/session-context.md`: what an anonymous caller receives, and no more `RequireAuthorization()`
      workaround needed for that.
- [ ] 3.2 The authorization page's statement of the 403 mapping.
- [ ] 3.3 `CHANGELOG.md` `[Unreleased]` → *Added* (the exception), *Changed* (401 for anonymous callers), *Fixed* (500).
