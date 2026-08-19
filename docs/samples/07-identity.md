# Sample 7 — Identity (external login + API keys)

> **Derived page.** The behaviour described here is specified by the `external-identity` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

**Concept**: how callers get **into** a Stratara app. External OpenID Connect sign-in with hardened
JIT provisioning for humans, API keys / PATs for machines, JWT-bearer for API tokens — all three
routed by the auth-scheme selector.

- **Code**: [`samples/Stratara.Sample.Identity`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.Identity)
- **Lines**: ~135
- **Read time**: 10–15 min
- **What it doesn't have**: no UI, no real identity provider — the OpenID Connect leg needs a live IdP, the API-key leg runs offline.

## What you'll see

1. **`SampleIdentityDbContext`** — an ordinary `IdentityDbContext<IdentityUser>` that calls
   `builder.ApplyIdentityDirectoryModel()` in `OnModelCreating`. This is the recommended hosting
   pattern: ASP.NET Identity's tables and Stratara's directory tables (`tenant_membership`,
   `active_tenant`, `setting_entry`, `api_key`) share **one context and one migration lineage**.
2. **The authentication chain** — `AddStrataraOpenIdConnect(configuration)` +
   `AddStrataraJwtBearer(configuration)` + `AddStrataraApiKey()`, fronted by
   `AddStrataraAuthSchemeSelector()`, which picks the scheme from the request's shape rather than
   from per-endpoint scheme lists.
3. **`AddStrataraExternalLoginProvisioning<IdentityUser>()`** — invoked from the OpenID Connect
   `OnTicketReceived` event, the moment the external identity is validated and before a local
   session is issued. The sample adds an invitation gate limiting provisioning to `@example.com`.
4. **The API-key lane** — `POST /admin/api-keys` issues a machine key; `GET /api/whoami` accepts it
   as the `X-Api-Key` header and reports the resolved actor and tenant.

## Running

```bash
dotnet run --project samples/Stratara.Sample.Identity
```

The host boots without a provider and binds to Kestrel's default port — check the launch log for
the actual address, typically `http://localhost:5000`. The API-key lane is the part you can drive
immediately:

```bash
KEY=$(curl -s -X POST http://localhost:5000/admin/api-keys | jq -r .apiKey)
curl -s -H "X-Api-Key: $KEY" http://localhost:5000/api/whoami
```

```json
{
  "actor": "019f6fa4-4ba1-7d8c-87ae-97c1e8064a93",
  "tenant": "11111111-1111-1111-1111-111111111111",
  "scheme": "resolved by the auth-scheme selector from the request shape"
}
```

A wrong key returns a flat `401`. To try a real login, point `Identity:OpenIdConnect` in
`appsettings.json` at your provider and browse to `/login`.

## Key takeaways

- The raw key (`stk_` + 32 CSPRNG bytes) is returned **exactly once**; storage holds only its
  SHA-256 digest, so a database leak yields no usable credential.
- Issuing a machine key materializes a `tenant_membership` row keyed by the key id — machines flow
  through the **same** membership → role → permission plane as humans. There is no parallel
  authorization path to keep in sync.
- External accounts link on the issuer's `sub`, never on the mutable email claim. Auto-linking to an
  existing local account requires the email to be verified by the provider **and** already confirmed
  locally; otherwise provisioning returns `RequiresInteractiveLinking` rather than merging.
- The ticket carries the same `stratara:tenant_id` claim the session-context middleware already
  reads — nothing downstream changes when you add a new sign-in path.

See the **[API Keys and Personal Access Tokens](../guides/api-keys-and-pats.md)** and
**[External Login (OpenID Connect) + JIT Provisioning](../guides/external-login-oidc.md)** guides for
the full walkthrough, and **[Sample 8](08-identity-directory.md)** for what an authenticated
identity is then allowed to do.
