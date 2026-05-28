# Stratara.Identity.Core

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Channel-agnostic identity primitives for the Stratara stack. Ships the shared model records, interfaces, and the typed `HttpClient` wrapper consumed by host-specific packages (e.g. `Stratara.Identity.AspNetCore` for server-side Blazor, with consumer-supplied implementations for non-web hosts such as mobile or desktop).

## What's in the box

| Folder | Contents |
|---|---|
| `Models/` | `AccessTokenInfo` (persisted token + expiry), `LoginRequest` / `LoginResponse` (HTTP payload shape), `ClaimsResponse` / `ClaimDto` (identity-endpoint claims), `StrataraSignInResult` (standalone, channel-agnostic sign-in outcome with localized failure message, token info, resolved user id, two-factor / lockout flags — no inheritance from `Microsoft.AspNetCore.Identity.SignInResult`) |
| `Abstractions/` | `IStrataraSignInManager` (per-channel sign-in dispatch), `IStrataraAuthenticationStateProvider` (auth-state surface), `ITokenStorage` (secure-storage abstraction), `IStrataraRedirectManager` (host-native post-auth redirect) |
| `HttpClientHelper.cs` | `IHttpClientHelper` + default impl — typed wrapper so identity services can depend on the right configured `HttpClient` (auth handler + base address) without coupling to specific names |

## Quick start

Reference this package from any host or library that needs to consume the Stratara identity surface (model records or the abstractions). Host-specific concrete implementations live in `Stratara.Identity.AspNetCore` for server-side Blazor; non-web host implementations are supplied by the consumer app.

## Dependencies

- `Stratara.Shared` — diagnostics, multitenancy types, session-context helpers used by the host-specific implementations downstream.

No ASP.NET Core / `Microsoft.AspNetCore.Identity` dependency by design — this package is consumable from MAUI, console, and unit-test contexts without dragging the ASP.NET runtime in transitively.
