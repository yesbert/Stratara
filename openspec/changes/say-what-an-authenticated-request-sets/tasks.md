## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.
      Done: approved by the owner on 2026-09-15, recorded at the owner's request.

## 1. Record what the middleware sets

- [x] 1.1 Confirm the delta matches the shipped behaviour before archiving it: the context an
      authenticated request receives carries the same actor and data-owner tenant, the claimed user as
      actor, and no data-owner user. Verify: `SessionContextMiddleware` in `src/Stratara.Sessions`
      and a test in `tests/Stratara.Infrastructure.Tests/Middlewares/SessionContextMiddlewareTests.cs`
      that asserts `UserId` is `null` for an authenticated request (add it if no test asserts it).
      Done: the middleware constructs the context with the resolved tenant as actor and data-owner tenant, the
      claimed user as actor user and `null` as data-owner user. No test asserted the absence;
      `InvokeAsync_AuthenticatedUser_SetsSessionContext` now asserts `ActorTenantId` equals `TenantId` and
      `UserId` is `null`. The middleware's XML remarks said "Actor = Subject" as well and now say what it sets.
      Infrastructure tests 348/348.
- [x] 1.2 `src/Stratara.Sessions/README.md` no longer describes the context as "Actor=Subject"; it
      says the tenants match, the claimed user is the actor, and the data-owner user is left unset.
      Verify: `grep -n "Actor=Subject" src/Stratara.Sessions/README.md` returns nothing.
      Done: the middleware bullet and *Adopting the Actor/Subject model* say it, with the reason.
- [x] 1.3 `docs/concepts/session-context.md` (from `document-every-capability`) already states the
      same; confirm the section *An authenticated HTTP request populates the context from its claims*
      agrees with the modified requirement once both are on `main`. Verify: a read of that section.
      Done: the section agreed on the tenants and the actor user and did not mention the data-owner user; it
      now says `UserId` is left unset and why. Documentation tests 699/699.

## 2. Close

- [ ] 2.1 `openspec validate say-what-an-authenticated-request-sets --strict` green; after archive,
      `openspec validate --specs --strict` green. Verify: the command output.
      Change validation green on 2026-09-15; the specs validation follows the archive.
- [ ] 2.2 Round-4 tracker entry R4-Arc-004 marked closed with the merge commit. Verify: the tracker.
