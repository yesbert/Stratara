# Stratara.Sessions

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

Concrete session-context provider + ASP.NET Core middleware for Stratara's Actor/Subject session model. Reads tenant + user identity from JWT claims (with optional `X-Tenant-Id` / `X-Client-Id` header fallbacks), populates the ambient `ISessionContextProvider`, and exposes the Actor/Subject pair to every request.

> **Since 3.0.10:** the `X-Tenant-Id` header fallback is **opt-in** via `SessionContextOptions.AllowTenantHeader = true` (default `false`). Without the gate, any authenticated principal could pick the tenant their request operated against in hosts whose JWT does not carry the tenant claim. Embed the tenant id in the JWT claim set, or opt in explicitly when an upstream platform-admin role check guards the header.

## Quick start

```csharp
// Program.cs / Startup.cs
builder.Services.AddSessionContext();

// In the middleware pipeline:
app.UseMiddleware<SessionContextMiddleware>();
```

Then resolve in any scoped service:

```csharp
public sealed class SomeHandler(ISessionContextProvider sessionContextProvider)
{
    public async Task HandleAsync(...) {
        var session = sessionContextProvider.Current
            ?? throw new InvalidOperationException("Session context not set");

        // session.TenantId        = Subject (data owner) — used for filtering / encryption AAD
        // session.UserId          = Subject user (nullable)
        // session.ActorTenantId   = Actor (who triggered) — audit trail
        // session.ActorUserId     = Actor user — audit trail
    }
}
```

## What's in the box

- `SessionContextProvider` — `internal sealed` impl of `ISessionContextProvider`, scoped per request. Writes Activity tags (`correlation.id`, `causation.id`, `tenant.id`, `user.id`) automatically on `Set` / `Clear`.
- `SessionContextMiddleware` — ASP.NET Core middleware that extracts tenant + user from `ClaimTypes.NameIdentifier` + `stratara:tenant_id` claim (with optional `X-Tenant-Id` header fallback gated by `SessionContextOptions.AllowTenantHeader`) and constructs a `SessionContext` with Actor=Subject (the default UserPlatform case).
- `SessionContextOptions` — configuration (`SessionContext` section) controlling the header fallback gate. Bind via `services.Configure<SessionContextOptions>(...)` or `services.AddOptions<SessionContextOptions>().Bind(...)`.
- `StrataraClaimTypes` — claim-name constants (`stratara:tenant_id`).
- `DefaultTenantIdentifier` — sentinel `Guid` used when no tenant claim or header is present (typically anonymous / system flows).
- `AddSessionContext()` DI extension — registers the concrete provider as a scoped service against `ISessionContextProvider`.

## Adopting the Actor/Subject model

For most operations Actor equals the data-owner Subject — a user acts on their own tenant's data. The split only diverges for:
- PlatformAdmin cross-tenant operations (Subject = customer tenant, Actor = admin tenant)
- Anonymous endpoints (Actor = `Guid.Empty`, Subject = the just-minted tenant)
- System / saga flows (Actor = `SessionContext.SystemActorTenantId` / `SystemActorUserId`)

Consumers that reject ambient context (libraries that prefer explicit `TenantId` parameters everywhere) do **not** need to take this package — `Stratara.Mediator` and the rest of the framework work without an `ISessionContextProvider` registered as long as no path requires it.

## Dependencies

- `Stratara.Abstractions` — for `ISessionContextProvider`.
- `Stratara.Contracts` — for the `SessionContext` record (wire-level).
- `Stratara.Diagnostics` — for `ApplicationDiagnostics` activity tags.
- `Microsoft.AspNetCore.Http.Abstractions` — for `HttpContext` / `RequestDelegate`.
- `Microsoft.Extensions.DependencyInjection.Abstractions`.
- `OpenTelemetry.Api`.
