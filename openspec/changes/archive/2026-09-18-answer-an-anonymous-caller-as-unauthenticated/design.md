## Context

`StrataraProblemDetailsExceptionHandler` (`src/Stratara.ServiceDefaults.AspNetCore/StrataraProblemDetailsExceptionHandler.cs:49-66`)
maps `StrataraValidationException` to 400 and `AuthorizationException` / `TenantAccessDeniedException` to
403, and returns `false` for everything else. The guards refuse a request without a session as a denial
(`AuthorizingMediator.cs:65-66`, `TenantIsolationGuard.cs:23-27`, `MembershipAuthorizationProviderOfTUser.cs:41-45`).
Unguarded paths throw `InvalidOperationException` at the sites listed in the proposal.

## Goals / Non-Goals

**Goals:** 401 for an anonymous caller at the opt-in boundary, on guarded and unguarded paths; a failure
type a host without the mapping can catch.

**Non-Goals:** changing what the guards throw (their exception types stay; only the boundary decides the
status); authenticating anybody; the session middleware's behaviour.

## Decisions

### D1 — One exception type, derived from `InvalidOperationException`

`SessionRequiredException : InvalidOperationException` in `Stratara.Abstractions.Session`, sealed, with
the standard constructors. Deriving keeps every existing catch and every log search on the message
working. Tier-A, so a host catches it without referencing any store or broker package.

### D2 — The status is decided at the boundary, from the principal

The handler asks `httpContext.User.Identity?.IsAuthenticated`. Not authenticated and one of the three
types → 401. Authenticated → denials 403 as today, `SessionRequiredException` not converted. Deciding it
at the boundary keeps the guards' contracts intact: a guard still says "denied"; whether the caller
should authenticate or give up is an HTTP question.

### D3 — Challenge through the host's scheme where there is one

**Decision.** If `IAuthenticationSchemeProvider.GetDefaultChallengeSchemeAsync()` returns a scheme, the
handler calls `httpContext.ChallengeAsync()` and then, if the response is a 401 that has not started,
writes the problem body; otherwise it sets 401 and writes the problem body itself.

**Why not always write 401 directly.** RFC 9110 requires a `WWW-Authenticate` header on 401, which the
scheme supplies; a bearer client relies on it.

**Why not always challenge.** Without a scheme `ChallengeAsync` throws. Under cookie authentication the
challenge redirects — which is what `[Authorize]` on the same endpoint would do, so the behaviour is the
host's own.

## Risks / Trade-offs

- [A client that treated 403 as "log in" now sees 401] → that is the correct signal; stated in the
  CHANGELOG under *Changed*.
