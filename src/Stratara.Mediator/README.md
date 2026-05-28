# Stratara.Mediator

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

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

## What's in the box

- `IMediator.HandleAsync<TResult>(IRequest<TResult>, CancellationToken)` — routes queries and commands-with-result to `IQueryHandler<TRequest, TResult>` through any registered `IPipelineBehavior<TRequest, TResult>` chain.
- `IMediator.HandleAsync<TRequest>(TRequest, CancellationToken)` — routes void commands to `ICommandHandler<TRequest>` through any registered `IPipelineBehavior<TRequest>` chain.
- `AuthorizingMediator` decorator — checks `[RequireRole]` attributes on the request type via `IAuthorizationProvider` before delegating to the inner mediator.
- `BucketLockPool` — concurrency primitive that serialises `IAggregateScopedCommand` dispatch per bucket id. Used by message-bus consumers (e.g. `Stratara.Infrastructure`'s `MediatorCommandWorker`) to keep aggregate writes single-writer.

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

## Dependencies

- `Stratara.Abstractions` — for `IMediator`/`IRequest`/`ICommand`/`IQuery`/`IPipelineBehavior` contracts.
- `Microsoft.Extensions.DependencyInjection.Abstractions`.
- `OpenTelemetry.Api` — emits an `Activity` per dispatch under the `Stratara.Application` source.

No EF Core, no message bus, no event sourcing. Library-safe.
