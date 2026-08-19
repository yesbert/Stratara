# External Login (OpenID Connect) + JIT Provisioning

> **Derived page.** The behaviour described here is specified by the `external-identity` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

`Stratara.Identity.AspNetCore` adds external identity providers — the "log in with Microsoft /
Keycloak / Google" flow — as ordinary ASP.NET Core authentication schemes, and provisions a local
account on a user's first sign-in. It is the third sign-in path alongside local passwords and API
keys, and it flows through the same membership, role, and permission planes as any human sign-in.

Two configuration-driven helpers cover the interactive and the API side; a hardened provisioning
service covers the account creation/linking. Every security-relevant default is on out of the box.

## The two schemes

`AddStrataraOpenIdConnect` wires the interactive authorization-code flow; `AddStrataraJwtBearer`
validates access tokens on API requests. Both are ordinary schemes, so they compose with the cookie,
API-key, and membership wiring via the auth-scheme selector.

```csharp
builder.Services
    .AddAuthentication(StrataraAuthSchemeSelectorOptions.SchemeName)
    .AddCookie(IdentityConstants.ApplicationScheme)
    .AddStrataraOpenIdConnect(builder.Configuration)   // Identity:OpenIdConnect
    .AddStrataraJwtBearer(builder.Configuration)        // Identity:JwtBearer
    .AddStrataraAuthSchemeSelector();                   // Bearer → JWT, else → cookie
```

Entra, Keycloak, and a generic OIDC provider differ only in configuration — the wiring is identical:

```json
{
  "Identity": {
    "OpenIdConnect": {
      "Authority": "https://login.microsoftonline.com/<tenant>/v2.0",
      "ClientId": "<client-id>",
      "ClientSecret": "<client-secret>"
    },
    "JwtBearer": {
      "Authority": "https://login.microsoftonline.com/<tenant>/v2.0",
      "Audience": "api://<your-api>",
      "ValidIssuers": [ "https://login.microsoftonline.com/<tenant>/v2.0" ]
    }
  }
}
```

A multi-issuer API (accepting tokens from several authorities) lists them all in `ValidIssuers`; the
token's `iss` selects the trusted authority. Both helpers keep the principal's name-identifier as the
issuer `sub`, so linking and lookups key on the stable subject — never on a mutable email.

## JIT provisioning

On a first external sign-in the user does not yet exist locally. `AddStrataraExternalLoginProvisioning`
registers a callable service that creates the account and links the external login — or links to an
existing account — from your sign-in callback:

```csharp
builder.Services.AddStrataraExternalLoginProvisioning<ApplicationUser>();
```

```csharp
// e.g. from the OpenID Connect OnTicketReceived event, or an external-login callback endpoint:
var info = await signInManager.GetExternalLoginInfoAsync();
var result = await provisioning.ProvisionAsync(info);

switch (result.Outcome)
{
    case ExternalLoginProvisioningOutcome.SignedInExisting:
    case ExternalLoginProvisioningOutcome.Linked:
    case ExternalLoginProvisioningOutcome.Provisioned:
        // result.User is ready — establish the local session.
        break;
    case ExternalLoginProvisioningOutcome.RequiresInteractiveLinking:
        // Same email, but not provably verified — prove ownership before linking.
        break;
    case ExternalLoginProvisioningOutcome.Denied:
        // Auto-provisioning off with no match, no email, or the invitation gate rejected it.
        break;
}
```

The service is channel-agnostic; the callback endpoint and any UI stay in your app.

## Why the defaults matter (nOAuth)

Naive external-login linking — "same email address means the same person" — is a known
account-takeover vector: some providers expose an email claim that is **mutable and unverified**, so
an attacker can set it to a victim's address and be linked to the victim's account. The provisioning
service defends against this with defaults you must deliberately relax:

| Invariant | Default |
|---|---|
| Link on the issuer's `(provider, sub)`, never on email | always |
| Auto-link to an existing account only when the email is verified by the provider (`email_verified` / Entra `xms_edov`) **and** already confirmed locally | `RequireVerifiedEmailForLinking = true` |
| Otherwise return `RequiresInteractiveLinking` — never a silent merge | always |
| Create an account for an unmatched sign-in | `AutoProvision = true` (set `false` to require pre-created/invited accounts) |
| Reject a sign-in via a custom policy (pending invitation, allow-list) | optional `InvitationGate` |
| Fail closed on any unsatisfied check | always |

Relaxing `RequireVerifiedEmailForLinking` re-opens the takeover vector — do it only for a provider
you fully trust.

## Composing with membership

Pair the provisioning with the membership sign-in bridge (`AddMembershipTenantClaim<TUser>()`) so the
provisioned user also carries the `stratara:tenant_id` claim and flows through tenant-scoped roles and
permissions — no change to the session-context middleware.

## See also

- The runnable `Stratara.Sample.Identity` sample wires all of the above end to end.
- [Authorization Decorators](auth-decorators.md) — role/permission enforcement the provisioned user flows through.
- [Enforce Tenant Isolation](enforce-tenant-isolation.md) — the tenant guard the `stratara:tenant_id` claim feeds.
