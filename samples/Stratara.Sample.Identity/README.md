# Stratara.Sample.Identity

How callers get **into** a Stratara app: **OpenID Connect** login with hardened **just-in-time (JIT)
provisioning** for humans, **API keys / PATs** for machines, and **JWT-bearer** validation for API
tokens — all three routed by the auth-scheme selector, all three ending in the same session shape.

Once a caller is authenticated, `Stratara.Sample.IdentityDirectory` picks up the story: what that
identity may actually *do*.

## What it shows

| Piece | API |
|---|---|
| Interactive external login | `AddStrataraOpenIdConnect(configuration)` |
| API access-token validation (multi-issuer by `iss`) | `AddStrataraJwtBearer(configuration)` |
| Machine-to-machine keys and PATs | `AddStrataraApiKey()` + `AddApiKeyStore<TContext>()` |
| Route by request shape (API key vs. Bearer vs. cookie) | `AddStrataraAuthSchemeSelector()` |
| Create/link the local account on first sign-in | `AddStrataraExternalLoginProvisioning<TUser>()` |

Provisioning runs from the OpenID Connect `OnTicketReceived` event — the moment the external identity
is validated, before a local session is issued.

The `SampleIdentityDbContext` shows the recommended hosting pattern: it is an ordinary
`IdentityDbContext<IdentityUser>` that calls `builder.ApplyIdentityDirectoryModel()` in
`OnModelCreating`, so the Stratara directory tables (`tenant_membership`, `active_tenant`,
`setting_entry`, `api_key`) share one context and **one migration lineage** with ASP.NET Identity's
own tables.

## The security invariants (on by default)

External account linking is the account-takeover-sensitive part (the *nOAuth* class of attacks: some
providers expose a mutable, unverified email claim). The provisioning service therefore:

- links on the issuer's **subject (`sub`)**, never on email;
- auto-links to an existing local account **only** when the email is verified by the provider **and**
  already confirmed locally — otherwise it returns `RequiresInteractiveLinking` and refuses to merge;
- honors an optional **invitation gate** (this sample only provisions `@example.com` addresses) and an
  `AutoProvision` switch;
- **fails closed** on every check it cannot satisfy.

Relaxing `RequireVerifiedEmailForLinking` re-opens the takeover vector — do it only for a fully
trusted provider.

## Run it

The host boots without a provider; only the external round-trip needs a live IdP. To try a real
login, set `Identity:OpenIdConnect` (Authority / ClientId / ClientSecret) in `appsettings.json` to
your provider, then:

```bash
dotnet run --project samples/Stratara.Sample.Identity
```

- `GET /` — endpoint overview
- `GET /login` — challenges the configured OpenID Connect provider
- `GET /api/me` — requires a valid `Authorization: Bearer <token>`
- `POST /admin/api-keys` — issues a machine key
- `GET /api/whoami` — authenticates with the `X-Api-Key` header

## API keys and PATs — no live provider needed

The API-key lane works offline, so it is the part you can drive right now. The host binds to
Kestrel's default port — check the launch log for the actual address, typically
`http://localhost:5000`:

```bash
KEY=$(curl -s -X POST http://localhost:5000/admin/api-keys | jq -r .apiKey)
curl -s -H "X-Api-Key: $KEY" http://localhost:5000/api/whoami
```

The raw key (`stk_` + 32 CSPRNG bytes) is returned **exactly once** — storage holds only its
SHA-256 digest, so a database leak yields no usable credential. A wrong key gets a flat 401; there
is no "unknown key" path that falls through to anonymous.

The important part is what *doesn't* happen: there is no parallel authorization path for machines.
Issuing a machine key materializes a `tenant_membership` row keyed by the key id, so the key's roles
resolve through exactly the same membership → role → permission plane a human actor uses — and
revoking the key removes that row. A **PAT** (pass `UserId` to `ApiKeyIssueRequest`) instead
authenticates *as* the bound user, carries no roles of its own, and requires that user to hold an
active membership at issuance.

## Composing with membership

The provisioned user needs a tenant before tenant-scoped roles mean anything. Pair this with the
membership sign-in bridge (`AddMembershipTenantClaim<TUser>()`) so every issued principal carries
the `stratara:tenant_id` claim that `SessionContextMiddleware` reads — see
[`Stratara.Sample.IdentityDirectory`](../Stratara.Sample.IdentityDirectory) for what that claim
unlocks.
