# Require an active membership for user-bound keys

> **Status:** approved (owner, 2026-11-06)

## Why

The consumer-visible effect today: a personal access token keeps authenticating, with its tenant
claim, after its user stopped being a member of that tenant. Issuing the token requires an active
membership, but validating it checks only that the key exists, is not revoked and has not expired. A
user whose membership became former or pending, or was removed, is still let in by the token, and an
endpoint that requires only authentication serves that tenant's data to them. Role and permission
checks do refuse such a request, which is why the gap is easy to miss: it is exactly the endpoints
without one that stay open.

## What Changes

- **Validating a key bound to a user requires that user's active membership in the key's tenant.**
  `IApiKeyStore.ValidateAsync` returns `null` for a token whose user's membership in the key's tenant
  is former, pending or missing, without saying why, as it does for an unknown, revoked or expired
  key. The API-key scheme then fails authentication, so the request carries no principal at all.
- **The token is not revoked.** Nothing is written when validation fails this way. The token validates
  again as soon as the membership is active again, unless it has been revoked or has expired meanwhile.
- **Machine keys are unaffected.** A key with no bound user validates as before.
- **The contract covers every store.** The check is part of what `IApiKeyStore.ValidateAsync` promises,
  so a custom implementation must make it too. The in-memory store in `Stratara.Testing` honours it.
- **Not breaking at compile time, and no schema change.** No public signature changes. It is a
  behaviour change a consumer can notice: a token that was still working for a person who has left a
  tenant now receives a 401.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `api-keys`: validation additionally fails for a user-bound key whose user holds no active membership
  in the key's tenant, and succeeds again when the membership returns.

`tenant-directory` is not modified: its statement that another scheme's principal keeps the tenant it
carries stays true, because such a key yields no principal. `design.md` records how the two fit.

## Impact

- **Behaviour:** `IApiKeyStore.ValidateAsync` and everything built on it, in particular the API-key
  authentication scheme of `Stratara.Identity.AspNetCore`.
- **Code:** `src/Stratara.Identity.EntityFrameworkCore/EfApiKeyStore.cs`,
  `src/Stratara.Testing/InMemoryApiKeyStore.cs`, and the XML documentation of
  `src/Stratara.Abstractions/Abstractions/ApiKeys/IApiKeyStore.cs` and
  `src/Stratara.Identity.AspNetCore/Authentication/ApiKeyAuthenticationHandler.cs`.
- **Documentation:** `docs/guides/api-keys-and-pats.md`, `docs/guides/tenant-membership.md`,
  `src/Stratara.Testing/README.md`.
- **Consumers with a custom `IApiKeyStore`:** must add the same check; the compiler will not tell them.
- **Data:** no migration. Existing tokens are subject to the check from the first validation after the
  upgrade.
- **Dissolved or superseded sources:** none.
