# API Keys and Personal Access Tokens

> **Derived page.** The behaviour described here is specified by the `api-keys` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

`Stratara.Abstractions.ApiKeys` declares the machine-to-machine authentication plane,
`Stratara.Identity.EntityFrameworkCore` implements it against your DbContext, and
`Stratara.Identity.AspNetCore` exposes it as an ordinary ASP.NET Core authentication scheme. It is
the third sign-in path alongside local passwords and external OpenID Connect logins.

The design goal is subtraction, not addition: an API key is **not** a second authorization system.
A presented key resolves to an actor and a tenant, and from there flows through the same
membership, role, and permission planes as a human sign-in — the same `stratara:tenant_id` claim,
the same session context, the same role and permission checks.

## Two kinds of key

`IApiKeyStore.IssueAsync` issues both. A **machine key** (no `UserId`) is its own actor and carries
tenant-scoped roles of its own; a **personal access token** (`UserId` set) acts *as* the bound user
and carries no roles at all — the user's memberships and roles apply:

```csharp
// Machine key — its own actor, with its own tenant-scoped roles.
var machine = await keys.IssueAsync(new ApiKeyIssueRequest(
    tenantId, Name: "ci-pipeline", Roles: ["Viewer"]));

// Personal access token — acts as the bound user; no roles of its own.
var pat = await keys.IssueAsync(new ApiKeyIssueRequest(
    tenantId, Name: "cli", Roles: [], UserId: userId, ExpiresAt: expiry));

Console.WriteLine(machine.RawKey);   // stk_… — shown once, unrecoverable afterwards
```

Both return `IssuedApiKey(RawKey, Descriptor)` — the `ApiKeyDescriptor` is the non-secret record you
list (`GetForTenantAsync`) and audit freely. Issuance is guarded: roles on a personal access token
throw, as does a token for a user without an **active membership** in the target tenant — a token
can never out-scope the user behind it.

## Bootstrapping a key the caller already knows

Issuance is the wrong shape when server and caller must share a key **before either boots** —
container orchestration, CI provisioning, self-hosted bundles, end-to-end test hosts. A key that
comes into existence at start-up and is only written to a log arrives too late: the calling side
reads its configuration when *it* starts. `ImportAsync` is that path:

```csharp
// Once, out of band — then keep the value in your secret store:
var rawKey = ApiKeyFormat.CreateRawKey();      // stk_…

// On every boot — idempotent, so it is safe to run unconditionally:
var descriptor = await keys.ImportAsync(new ApiKeyImportRequest(
    rawKey, tenantId, Name: "bootstrap-admin", Roles: ["Admin"]));
```

The imported key is stored exactly like an issued one, membership row included, so nothing
downstream can tell the two apart. Import is a **machine-key** path only — `ApiKeyImportRequest`
has no `UserId`, because a personal access token acts as its bound user and is issued by that user.

Three properties make this safe to run on every start of every replica:

- **Idempotent.** Importing a value that is already stored returns the existing descriptor. The
  stored key is never mutated, so a changed configuration cannot escalate a key's roles or extend
  its expiry unnoticed — a differing name, role set, or expiry is logged and otherwise ignored.
  Concurrent replicas racing the same first import converge on one key rather than failing.
- **Never resurrecting.** A revoked or expired key value is rejected, not silently reinstated, and a
  value already bound to another tenant cannot be re-bound.
- **Format-checked.** Only canonical values are accepted — this is what keeps the unsalted digest
  defensible (see below). Generate them with `ApiKeyFormat.CreateRawKey()`; a hand-picked value like
  `stk_dev-local` is refused.

Do not log an imported key. Unlike the one-shot value from `IssueAsync`, it is a durable
configuration secret and belongs in the same place as your database password.

## No parallel authorization path

This is the load-bearing decision. Issuing a machine key writes a `tenant_membership` row keyed by
the **key id** (`UserId` = key id) carrying the key's roles. Role checks, permission resolution,
and cross-tenant authorization then treat the key exactly like any other actor — there is no
key-specific branch anywhere in the authorization stack to audit or forget. `RevokeAsync` and the
erasure sweeps (`RemoveAllForTenantAsync`, `RemoveAllForUserAsync`) remove those rows again, so
revoking a key removes its access, not merely its credential. Personal access tokens write no
membership row — they ride the user's existing one.

## Wiring the scheme

```csharp
builder.Services
    .AddTenantMembershipStore<AppDbContext>()
    .AddApiKeyStore<AppDbContext>();          // table api_key, unique index on the hash

builder.Services
    .AddAuthentication(StrataraAuthSchemeSelectorOptions.SchemeName)
    .AddCookie(IdentityConstants.ApplicationScheme)
    .AddStrataraApiKey()                       // scheme "StrataraApiKey", header X-Api-Key
    .AddStrataraAuthSchemeSelector();
```

The ticket carries `ClaimTypes.NameIdentifier` (the key id for machine keys, the user id for
personal access tokens), `ClaimTypes.Name` (the key's display name), and `stratara:tenant_id` —
exactly the claims `SessionContextMiddleware` already reads. Nothing downstream changes.

## Issue, then use

```bash
# Issue — the raw key appears exactly once, in this response:
curl -sX POST http://localhost:5000/admin/api-keys
# → { "apiKey": "stk_9f3c…", "keyId": "0199…", "roles": ["Viewer"] }

# Authenticate with it:
curl -s http://localhost:5000/api/whoami -H "X-Api-Key: stk_9f3c…"
# → { "actor": "0199…", "tenant": "1111…" }
curl -s http://localhost:5000/api/whoami -H "X-Api-Key: stk_bogus"   # → 401
```

The issuance endpoint is yours to guard — the sample leaves it open for convenience; a real host
puts it behind an administrator role or permission.

## Routing mixed API and browser hosts

`AddStrataraAuthSchemeSelector` is a policy scheme that picks the handler from the request's shape:
API-key header present → the API-key scheme; `Authorization: Bearer …` → the bearer scheme;
everything else → the cookie fallback. Set it as the default and no endpoint needs a scheme list:

```csharp
.AddStrataraAuthSchemeSelector(o =>
{
    o.BearerScheme = JwtBearerDefaults.AuthenticationScheme;  // default: Identity's bearer scheme
    o.FallbackScheme = IdentityConstants.ApplicationScheme;
    o.ApiKeyHeaderName = "X-Api-Key";
});
```

## Why the defaults matter

| Invariant | Default |
|---|---|
| Raw key = `stk_` + Base64Url over 32 CSPRNG bytes; the prefix helps secret scanners flag leaks | always |
| The raw key is returned once and never persisted | always |
| An imported key must match that exact format | always — `ImportAsync` rejects anything else |
| A repeated import mutates the stored key | never — it returns the existing descriptor |
| An import reinstates a revoked or expired key | never — it throws |
| Storage holds only the SHA-256 hex digest | always — deliberately unsalted |
| Unknown, revoked, or expired key → authentication fails, never falls through to anonymous | always (fail-closed) |
| Machine-key roles resolve through `tenant_membership` like any actor's | always — no parallel authorization path |
| A personal access token carries its own roles | never — issuance throws |
| A personal access token requires the bound user's active membership | always |
| The key is read from the request header | `HeaderName = "X-Api-Key"` |
| The key is also read from the `access_token` query parameter | `AllowQueryStringKey = false` |

The unsalted digest is a considered tradeoff, not an oversight. Salting defends **low-entropy**
secrets — passwords — against precomputed tables. A key here carries 256 bits of CSPRNG entropy, so
no rainbow table is feasible regardless of salt, and the deterministic digest is what makes
validation an O(1) hit on a unique index rather than a scan over every stored key. The property
that matters — a database leak yields no usable credential — holds either way.

That reasoning is also why `ImportAsync` enforces the format instead of taking any string: entropy
cannot be measured, but shape can. A value that is structurally incapable of carrying 256 bits would
turn the unsalted digest into a guessable one — in the same table where every other key is strong,
and without any visible symptom. Generating the value with `ApiKeyFormat.CreateRawKey()` costs one
line and keeps the guarantee intact for imported keys too.

`AllowQueryStringKey` is off for good reason: query strings land in access logs, proxy logs,
referrer headers, and browser history. Enable it only for transports that cannot set headers
(WebSocket negotiation, for example), and treat those keys as short-lived.

## See also

- The runnable `Stratara.Sample.Identity` sample issues a key and authenticates with it end to end.
- [Tenant Membership and the Sign-In Tenant Claim](tenant-membership.md) — the plane a key's roles resolve through.
- [External Login (OpenID Connect) + JIT Provisioning](external-login-oidc.md) — the interactive sign-in path alongside this one.
