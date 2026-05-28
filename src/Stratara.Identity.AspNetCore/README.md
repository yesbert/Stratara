# Stratara.Identity.AspNetCore

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Channel-agnostic ASP.NET Core identity wiring for the Stratara stack. Provides the `AddAspNetIdentity` / `AddAspNetIdentityWithSignInManager` extension methods and an `IStrataraSignInManager` wrapper around the ASP.NET Core `SignInManager`. Channel-specific glue (Blazor Server's `AuthenticationStateProvider`, MAUI session-state forwarders, etc.) is the consumer's responsibility — Stratara intentionally stops at the ASP.NET-Core-generic surface to stay application-agnostic.

## What's in the box

| Folder | Contents |
|---|---|
| `DependencyInjection/AspCoreIdentityServiceCollectionExtensions` | `AddAspNetIdentity<TUser, TIdentityDbContext>()` (Stratara password/lockout/schema-v3/passkey defaults), `AddAspNetIdentityWithSignInManager<TUser, TIdentityDbContext>()` (same + `AspNetSignInManager` + localization), `AddDevelopmentNoOpEmailSender<TUser>()` (dev-only, throws in Production) |
| `Services/AspNetSignInManager<TUser>` | Wraps `SignInManager<TUser>` + `UserManager<TUser>` and produces `StrataraSignInResult` with already-localized failure messages |
| `Services/IdentityNoOpEmailSender<TUser>` | Development-time email sender that drops every email (`Task.CompletedTask`); replace in production |
| `Resources/IdentityResources` | Resource-anchor for sign-in failure messages. English default ships in `IdentityResources.resx`; `IdentityResources.de.resx` provides German overrides. `AddAspNetIdentityWithSignInManager` calls `AddLocalization()` so `IStringLocalizer<IdentityResources>` resolves automatically. |

## Localization

`AspNetSignInManager` resolves its five user-facing failure messages (`Identity.SignIn.Lockout`, `NotAllowed`, `InvalidCredentials`, `InvalidTwoFactor`, `InvalidRecoveryCode`) via `IStringLocalizer<IdentityResources>`. Languages out of the box: **English** (default) and **German** (`de`). To add another culture, ship a satellite `.resx` (e.g. `IdentityResources.fr.resx`) in your own assembly and register a chained `IStringLocalizer<IdentityResources>` if needed. Selection follows `CultureInfo.CurrentUICulture` — wire up `app.UseRequestLocalization(...)` to map this from the request.

## Quick start

```csharp
// Channel-agnostic ASP.NET Core host (MVC, Razor Pages, Minimal API, ...):
builder.AddAspNetIdentityWithSignInManager<ApplicationUser, IdentityDbContext>();

// Or for a host without sign-in manager (e.g. a worker that only needs identity stores):
builder.AddAspNetIdentity<ApplicationUser, IdentityDbContext>();
```

For Blazor Server hosts, additionally register your own `IStrataraAuthenticationStateProvider` implementation (and the `AuthenticationStateProvider` forwarder). Stratara does not ship a Blazor-specific provider — the previous `BlazorAuthenticationStateProvider` lived here in 1.x but moved out in v2.0.0 to keep this package application-agnostic.

## Dependencies

- `Stratara.Identity.Core` — channel-agnostic abstractions (`IStrataraSignInManager`, `IStrataraAuthenticationStateProvider`) + shared model records.
- `Stratara.Shared` — multitenancy + session-context types.
- `Microsoft.AspNetCore.App` — shared framework reference for `SignInManager`, `IEmailSender<TUser>`, etc.
- `Microsoft.AspNetCore.Identity.EntityFrameworkCore` — ASP.NET Identity stores.
- `Microsoft.IdentityModel.JsonWebTokens`, `System.IdentityModel.Tokens.Jwt` — JWT helpers for token-based flows.
