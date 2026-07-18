# Stratara.Identity.AspNetCore

> **License:** [MIT](../../LICENSE).

Channel-agnostic ASP.NET Core identity wiring for the Stratara stack. Provides the `AddAspNetIdentity` / `AddAspNetIdentityWithSignInManager` extension methods and an `IStrataraSignInManager` wrapper around the ASP.NET Core `SignInManager`. Channel-specific glue (Blazor Server's `AuthenticationStateProvider`, MAUI session-state forwarders, etc.) is the consumer's responsibility — Stratara intentionally stops at the ASP.NET-Core-generic surface to stay application-agnostic.

## What's in the box

| Folder | Contents |
|---|---|
| `DependencyInjection/AspCoreIdentityHostBuilderExtensions` | `AddAspNetIdentity<TUser, TIdentityDbContext>()` (Stratara password/schema-v3/passkey defaults — **no lockout**), `AddAspNetIdentityWithSignInManager<TUser, TIdentityDbContext>()` (same + **lockout defaults** + `AspNetSignInManager` + localization), `AddDevelopmentNoOpEmailSender<TUser>()` (dev-only, throws in Production) |

> **Lockout only ships with the sign-in manager.** `ApplyStrataraLockoutDefaults` runs inside
> `AddAspNetIdentityWithSignInManager` only — the bare `AddAspNetIdentity` leaves ASP.NET Identity's
> own lockout defaults in place. If you wire sign-in yourself on top of `AddAspNetIdentity`,
> configure `IdentityOptions.Lockout` explicitly; otherwise password attempts are not throttled the
> way the rest of this package assumes.
| `Services/AspNetSignInManager<TUser>` | Wraps `SignInManager<TUser>` + `UserManager<TUser>` and produces `StrataraSignInResult` with already-localized failure messages |
| `Services/IdentityNoOpEmailSender<TUser>` | Development-time email sender that drops every email (`Task.CompletedTask`); replace in production |
| `Resources/IdentityResources` | Resource-anchor for sign-in failure messages. English default ships in `IdentityResources.resx`; `IdentityResources.de.resx` provides German overrides. `AddAspNetIdentityWithSignInManager` calls `AddLocalization()` so `IStringLocalizer<IdentityResources>` resolves automatically. |
| `DependencyInjection/MembershipClaimsServiceCollectionExtensions` + `Services/MembershipClaims*` | Sign-in tenant-claim bridge: `AddMembershipTenantClaim<TUser>()` (stamp `stratara:tenant_id` at issuance) and `AddMembershipTenantClaimsTransformation()` (resolve live per request) |
| `Authorization/*` + `DependencyInjection/PermissionPolicyServiceCollectionExtensions` | `AddStrataraPermissionPolicies()` — every catalog permission becomes an on-demand `[Authorize("...")]` policy backed by `IPermissionResolver` |
| `Authentication/ApiKey*` + `DependencyInjection/ApiKeyAuthenticationExtensions` | `AddStrataraApiKey()` (X-Api-Key scheme over `IApiKeyStore`) and `AddStrataraAuthSchemeSelector()` (route API-key vs. Bearer vs. cookie by request shape) |
| `Authentication/Stratara{OpenIdConnect,JwtBearer}Options` + `DependencyInjection/OpenIdConnectAuthenticationExtensions` | `AddStrataraOpenIdConnect(configuration)` (interactive external login) and `AddStrataraJwtBearer(configuration)` (API access-token validation, multi-issuer by `iss`) |
| `Services/ExternalLoginProvisioningService<TUser>` + `DependencyInjection/ExternalLoginProvisioningExtensions` | `AddStrataraExternalLoginProvisioning<TUser>()` — hardened JIT create/link of local accounts on first external sign-in (see below) |

## Localization

`AspNetSignInManager` resolves its four user-facing failure messages (`Identity.SignIn.Lockout`, `Identity.SignIn.InvalidCredentials`, `Identity.SignIn.InvalidTwoFactor`, `Identity.SignIn.InvalidRecoveryCode`) via `IStringLocalizer<IdentityResources>`. A "not allowed" sign-in deliberately maps onto the `InvalidCredentials` message rather than getting its own, to avoid confirming that an account exists. Languages out of the box: **English** (default) and **German** (`de`). To add another culture, ship a satellite `.resx` (e.g. `IdentityResources.fr.resx`) in your own assembly and register a chained `IStringLocalizer<IdentityResources>` if needed. Selection follows `CultureInfo.CurrentUICulture` — wire up `app.UseRequestLocalization(...)` to map this from the request.

## Quick start

```csharp
// Channel-agnostic ASP.NET Core host (MVC, Razor Pages, Minimal API, ...):
builder.AddAspNetIdentityWithSignInManager<ApplicationUser, IdentityDbContext>();

// Or for a host without sign-in manager (e.g. a worker that only needs identity stores):
builder.AddAspNetIdentity<ApplicationUser, IdentityDbContext>();
```

For Blazor Server hosts, additionally register your own `IStrataraAuthenticationStateProvider` implementation (and the `AuthenticationStateProvider` forwarder). Stratara does not ship a Blazor-specific provider — the previous `BlazorAuthenticationStateProvider` lived here in 1.x but moved out in v2.0.0 to keep this package application-agnostic.

## External login (OpenID Connect) + JIT provisioning

Add external identity providers as ordinary authentication schemes and provision local accounts on
first sign-in:

```csharp
builder.Services
    .AddAuthentication(StrataraAuthSchemeSelectorOptions.SchemeName)
    .AddCookie(IdentityConstants.ApplicationScheme)
    .AddStrataraOpenIdConnect(builder.Configuration)   // interactive "log in with <provider>"
    .AddStrataraJwtBearer(builder.Configuration)        // API access-token validation (iss-routed)
    .AddStrataraAuthSchemeSelector();                   // route Bearer vs. cookie per request

builder.Services.AddStrataraExternalLoginProvisioning<ApplicationUser>();
```

`AddStrataraOpenIdConnect` binds `Identity:OpenIdConnect` (Authority, ClientId, ClientSecret,
Scopes) and `AddStrataraJwtBearer` binds `Identity:JwtBearer` (Authority, Audience, ValidIssuers).
Both key the principal on the issuer `sub`, never on email — Entra, Keycloak, and generic OIDC differ
only in configuration.

`ExternalLoginProvisioningService<TUser>` creates or links the local account on a first external
sign-in with the account-takeover defenses on by default: it links on the issuer's `(provider, sub)`;
auto-links to a pre-existing account **only** when the email is verified by the provider
(`email_verified`/`xms_edov`) **and** already confirmed locally — otherwise it returns
`RequiresInteractiveLinking` and refuses to merge; honors an optional invitation gate and an
`AutoProvision` switch; and fails closed. Call it from your sign-in callback (for example the OpenID
Connect `OnTicketReceived` event). The `Stratara.Sample.Identity` sample shows the full wiring.

## Dependencies

- `Stratara.Identity.Core` — channel-agnostic abstractions (`IStrataraSignInManager`, `IStrataraAuthenticationStateProvider`) + shared model records.
- `Stratara.Shared` — multitenancy + session-context types.
- `Microsoft.AspNetCore.App` — shared framework reference for `SignInManager`, `IEmailSender<TUser>`, etc.
- `Microsoft.AspNetCore.Identity.EntityFrameworkCore` — ASP.NET Identity stores.
- `Microsoft.AspNetCore.Authentication.OpenIdConnect`, `Microsoft.AspNetCore.Authentication.JwtBearer` — external-login OIDC + API bearer-token schemes.
- `Microsoft.IdentityModel.JsonWebTokens`, `System.IdentityModel.Tokens.Jwt` — JWT helpers for token-based flows.
