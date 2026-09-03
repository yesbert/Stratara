# Stratara.Mediator

> **Derived.** The behaviour described here is specified under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them.

> **License:** [MIT](../../LICENSE).

In-process mediator with DI-resolved handlers and pipeline behaviors. Drop-in replacement for MediatR-style routing without the runtime cost of `MethodInfo.Invoke` — uses a typed wrapper cache and direct DI dispatch.

## Quick start

```csharp
services.AddMediator()
    .AddCommandHandlersFromAssemblyContaining<Program>()
    .AddQueryHandlersFromAssemblyContaining<Program>()
    .AddPipelineBehaviorWithResult(typeof(LoggingBehavior<,>))
    .AddPipelineBehavior(typeof(LoggingBehavior<>));

// Optional: wrap in authorization decorator
services.AddAuthorizingMediator<MyAuthorizationProvider>();
```

Every dispatch is traced as a span named `Handle <RequestType>`. The spans come from the
`Stratara.Application` activity source — subscribe to it and they appear; register your own
OpenTelemetry `Tracer` and the mediator uses that instead. Nothing has to be registered for the
mediator to start.

`IMediator` is registered **scoped**. Resolve it from a scope (a request scope in ASP.NET Core, or
an explicit `IServiceProvider.CreateScope()` in a console host) — resolving it from the root
provider throws.

## What's in the box

- `IMediator.HandleAsync<TResult>(IRequest<TResult>, CancellationToken)` — routes queries and commands-with-result to `IQueryHandler<TRequest, TResult>` through any registered `IPipelineBehavior<TRequest, TResult>` chain.
- `IMediator.HandleAsync<TRequest>(TRequest, CancellationToken)` — routes void commands to `ICommandHandler<TRequest>` through any registered `IPipelineBehavior<TRequest>` chain.
- `AuthorizingMediator` decorator — checks `[RequireRole]` attributes via `IAuthorizationProvider` and `[RequirePermission]` attributes via `IPermissionResolver` (both AND) on the request's **runtime** type before delegating to the inner mediator. Its startup validator fails fast when a permission-guarded type is registered without an authorizing mediator or without a resolver, so a guard can never be silently skipped.
- `BucketLockPool` — concurrency primitive that serialises `IAggregateScopedCommand` dispatch per bucket id. Used by message-bus consumers (e.g. the `MediatorCommandWorker` in `Stratara.Outbox.RabbitMQ`) to keep aggregate writes single-writer.

## Pipeline behavior contract

Behaviors run outer-to-inner in DI registration order:

```csharp
public sealed class LoggingBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>
    where TRequest : IRequest<TResult>
{
    public async Task<TResult> HandleAsync(
        TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        // before
        var result = await next();
        // after
        return result;
    }
}
```

## Tenant isolation

`AddStrataraTenantIsolation()` registers a pipeline behavior that enforces tenant isolation at the
mediator entrance — *before* the handler runs — for any request that opts in by implementing the
`ITenantScopedRequest` marker. Requests that do not implement the marker pass through untouched.

```csharp
public sealed record GetCustomerQuery(Guid CustomerId, Guid TenantId)
    : IQuery<CustomerDto>, ITenantScopedRequest;

services
    .AddStrataraValidation()           // validation stays outermost
    .AddStrataraTenantIsolation();     // then tenant isolation
```

The behavior compares the request's `TenantId` (the *data owner*) against the ambient session's
data-owner tenant (`SessionContext.TenantId`), not the *actor* tenant (`SessionContext.ActorTenantId`).
A request whose payload names a different tenant than the established session subject is rejected with
`TenantAccessDeniedException` (translated to HTTP 403 on ASP.NET hosts that register
`AddStrataraProblemDetails()` from `Stratara.ServiceDefaults.AspNetCore`; surfaced through the
message-failure path on workers).

### Default vs. strict mode

- **`TenantIsolationMode.Default`** — enforces only the subject match. A privileged cross-tenant
  operation (actor tenant ≠ data-owner tenant) passes, because the calling endpoint is expected to have
  promoted the session's data-owner tenant to the target before dispatch.
- **`TenantIsolationMode.Strict`** — additionally routes every cross-tenant operation through an
  `ICrossTenantAuthorizer`. Stratara registers a **deny-all** default (via `TryAdd`), so strict mode
  rejects all cross-tenant access until you register your own authorizer that grants it:

```csharp
services.AddStrataraTenantIsolation(o => o.Mode = TenantIsolationMode.Strict);
services.AddScoped<ICrossTenantAuthorizer, PlatformAdminCrossTenantAuthorizer>();
```

```csharp
internal sealed class PlatformAdminCrossTenantAuthorizer(IHttpContextAccessor http)
    : ICrossTenantAuthorizer
{
    public ValueTask<bool> IsCrossTenantAllowedAsync(SessionContext session, CancellationToken ct) =>
        ValueTask.FromResult(http.HttpContext?.User.IsInRole("PlatformAdmin") ?? false);
}
```

> The behavior runs both in-process (queries via `IMediator` at the endpoint, where `HttpContext` is
> available) and worker-side (commands dispatched through the outbox, where there is no `HttpContext`).
> An `ICrossTenantAuthorizer` that needs request-role state should be applied on the in-process path;
> the worker path must base its decision on the `SessionContext` alone.

## Dependencies

- `Stratara.Abstractions` — for `IMediator`/`IRequest`/`ICommand`/`IQuery`/`IPipelineBehavior` contracts,
  plus `ITenantScopedRequest`/`ICrossTenantAuthorizer`/`TenantAccessDeniedException`.
- `Stratara.Diagnostics` — log-event IDs for the tenant-isolation behavior.
- `Microsoft.Extensions.DependencyInjection.Abstractions`.
- `Microsoft.Extensions.Logging.Abstractions`.
- `OpenTelemetry.Api` — emits an `Activity` per dispatch under the `Stratara.Application` source.

No EF Core, no message bus, no event sourcing. Library-safe.
