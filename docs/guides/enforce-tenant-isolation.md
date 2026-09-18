---
title: "Enforce Tenant Isolation"
description: "The mediator-entrance guard that rejects a request naming another tenant before your handler runs, and the modes it can run in."
---

# Enforce Tenant Isolation

> **Derived page.** The behaviour described here is specified by the `tenant-isolation` and
> `authorization` capabilities under `openspec/specs/`. Those specifications are the source; this page
> explains and illustrates them. Where the two disagree, the specification is right and this page is a
> bug.

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
it without referencing the behavior. On ASP.NET hosts the framework maps it to **HTTP 403 (Forbidden)**
alongside `AuthorizationException`, in one RFC 7807 problem shape — or to **HTTP 401** when the caller is
not authenticated, because what that caller lacks is an identity. The mapping is opt-in — a host that
does not register it keeps its own error model, and the exception reaches its own diagnostics
unchanged:

```csharp
builder.Services.AddStrataraProblemDetails();   // Stratara.ServiceDefaults.AspNetCore
app.UseExceptionHandler();
```

## Filter tenant-scoped rows at the database as well

The entrance guard covers requests. It does not cover a query that reaches your database context some
other way: a background job, a projection helper, a repository method a handler calls with the wrong
id. The second layer, the tenant query filter, is independent on purpose. It constrains **every**
query a tenant-scoped context issues, including the ones the guard never saw.

Two declarations and one call switch it on:

- **Mark the entity** with `IMultiTenant` (`Stratara.Abstractions.Entities`), which gives it a
  `TenantId`. Marking is your decision: an entity that does not implement it gets no filter.
- **Make the context tenant-scoped** by implementing `ITenantScopedDbContext`
  (`Stratara.EventSourcing.EntityFrameworkCore.Abstractions`), whose `TenantId` is the ambient tenant.
  Usually that is the data-owner tenant of the current session.
- **Call `ApplyGlobalTenantQueryFilters(this)`** on the `ModelBuilder` in `OnModelCreating`, after
  the entities are in the model. It installs a filter on every entity type that implements
  `IMultiTenant` at that moment.

```csharp
using Stratara.Abstractions.Entities;
using Stratara.Abstractions.Session;
using Stratara.EventSourcing.EntityFrameworkCore.Abstractions;
using Stratara.EventSourcing.EntityFrameworkCore.Extensions;

public sealed class InvoiceView : IMultiTenant
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public decimal Total { get; set; }
}

public sealed class InvoiceReadContext(
    DbContextOptions<InvoiceReadContext> options,
    ISessionContextProvider sessions)
    : DbContext(options), ITenantScopedDbContext
{
    public Guid TenantId => sessions.Current?.TenantId ?? Guid.Empty;

    public DbSet<InvoiceView> Invoices => Set<InvoiceView>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InvoiceView>();
        modelBuilder.ApplyGlobalTenantQueryFilters(this);   // last: filters what is already mapped
    }
}
```

A query through that context returns only rows whose `TenantId` matches the context's `TenantId`.
`context.Invoices.ToListAsync()` cannot return another tenant's invoice, whatever the calling code
forgot to check. A context without a session resolves to the empty identifier and sees no tenant's
rows. Entity Framework Core's own `IgnoreQueryFilters()` still switches the filter off for a single
query, so treat that call the way you would treat any cross-tenant operation.

## Related

- **[Write a Command Handler](write-a-command-handler.md)** — the handler the behavior guards.
- **[Authorization Decorators](auth-decorators.md)** — role-based `[RequireRole]` enforcement, the other half of the entrance guard.
- **[DI Extensions Cheat Sheet](../reference/di-extensions-cheatsheet.md)** — `AddStrataraTenantIsolation()` at a glance.
