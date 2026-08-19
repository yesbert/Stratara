# Enforce Tenant Isolation

> **Derived page.** The behaviour described here is specified by the `tenant-isolation` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

`Stratara.Mediator` runs request-level tenant enforcement as a **mediator pipeline behavior**: a
request that opts in is checked *before* the handler, so a command or query naming a tenant other
than the caller's data-owner tenant never reaches your domain logic. It is the command-/query-entrance
complement to the database-side tenant query filters (`ApplyGlobalTenantQueryFilters`) — defence in
depth, enforced at the front door instead of relying on every handler to filter correctly.

## The marker

`ITenantScopedRequest` lives in `Stratara.Abstractions.Multitenancy` (so you can mark a request
without referencing the behavior). It exposes the tenant the request operates on:

```csharp
using Stratara.Abstractions.Multitenancy;

public interface ITenantScopedRequest
{
    Guid TenantId { get; }
}
```

A request implements both its CQRS contract and the marker. The behavior acts only on requests that
implement `ITenantScopedRequest`; everything else passes through untouched (opt-in).

```csharp
public sealed record GetAccountQuery(Guid AccountId, Guid TenantId)
    : IQuery<AccountDto>, ITenantScopedRequest;
```

## Subject, not Actor

The behavior compares the request's `TenantId` against the **Subject** (the data-owner
`SessionContext.TenantId`), *not* the **Actor** (`SessionContext.ActorTenantId`). For the 95% of
requests where actor and subject coincide, that's just "you may only touch your own tenant". The
distinction matters only for privileged cross-tenant operations — see *Strict mode* below. (See
**[Actor vs Subject](../overview/glossary.md#actor-vs-subject)** for the session model.)

## Default mode — subject match

| Situation | Result |
|---|---|
| `request.TenantId == session.TenantId` | Passes to the handler. |
| `request.TenantId != session.TenantId` | Blocked — the pipeline throws `TenantAccessDeniedException`. |

A privileged cross-tenant operation (actor tenant ≠ data-owner tenant) still passes in default mode,
because the calling endpoint is expected to have promoted the session's data-owner tenant to the
target before dispatch. Default mode therefore guards against a *payload-forged* tenant id without
getting in the way of a legitimate admin flow.

## Strict mode — gate the cross-tenant case

`TenantIsolationMode.Strict` keeps the subject check **and** routes every cross-tenant operation
(actor tenant ≠ data-owner tenant) through an `ICrossTenantAuthorizer`. The shipped default denies
all, so strict mode forbids cross-tenant access until you register an authorizer that grants it:

```csharp
using Stratara.Abstractions.Multitenancy;
using Stratara.Contracts.Session;

internal sealed class PlatformAdminCrossTenantAuthorizer(IHttpContextAccessor http)
    : ICrossTenantAuthorizer
{
    public ValueTask<bool> IsCrossTenantAllowedAsync(SessionContext session, CancellationToken ct) =>
        ValueTask.FromResult(http.HttpContext?.User.IsInRole("PlatformAdmin") ?? false);
}
```

> The behavior runs both in-process (queries via `IMediator` at the endpoint, where `HttpContext` is
> available) and worker-side (commands dispatched through the outbox, where there is no `HttpContext`).
> An authorizer that reads request-role state belongs on the in-process path; the worker path must
> decide from the `SessionContext` alone.

## Register it

Call `AddStrataraTenantIsolation()` **after** `AddStrataraValidation()` so validation stays the
outermost behavior and tenant isolation runs just inside it, still before the handler:

```csharp
builder.Services
    .AddMediator()
    .AddStrataraValidation()
    .AddStrataraTenantIsolation(o => o.Mode = TenantIsolationMode.Strict)
    .AddCommandHandlersFromAssemblyContaining<Program>()
    .AddQueryHandlersFromAssemblyContaining<Program>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICrossTenantAuthorizer, PlatformAdminCrossTenantAuthorizer>();
```

## Map the failure to an HTTP response

`TenantAccessDeniedException` is declared in `Stratara.Abstractions.Multitenancy`, so a host can catch
it without referencing the behavior. On ASP.NET hosts the framework's
`AuthorizationExceptionMiddleware` already maps it to **HTTP 403 (Forbidden)** alongside
`AuthorizationException`.

## Related

- **[Write a Command Handler](write-a-command-handler.md)** — the handler the behavior guards.
- **[Authorization Decorators](auth-decorators.md)** — role-based `[RequireRole]` enforcement, the other half of the entrance guard.
- **[DI Extensions Cheat Sheet](../reference/di-extensions-cheatsheet.md)** — `AddStrataraTenantIsolation()` at a glance.
