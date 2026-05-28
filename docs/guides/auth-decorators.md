# Authorization Decorators

Stratara enforces role-based authorization at the mediator boundary via `[RequireRole]` + `AuthorizingMediator` — so every command / query goes through a single, mandatory check, regardless of which channel delivered it (HTTP, MAUI, console, worker).

## Mark a command

```csharp
using Stratara.Abstractions.Authorization;

[RequireRole("BankTeller")]
public sealed record DepositCommand(Guid AccountId, decimal Amount) : ICommand;
```

If the active `SessionContext.ActorRoles` doesn't contain `BankTeller`, `AuthorizingMediator.HandleAsync(…)` throws `UnauthorizedAccessException` **before** the handler is resolved.

## Multiple roles

```csharp
[RequireRole("BankTeller", "BackOffice")]
public sealed record CloseAccountCommand(Guid AccountId) : ICommand;
```

Default semantics: **any one of the listed roles** is sufficient (`OR`). For `AND` semantics, layer multiple attributes:

```csharp
[RequireRole("BankTeller")]
[RequireRole("Supervisor")]   // must have both
public sealed record HighValueTransferCommand(Guid FromAccountId, Guid ToAccountId, decimal Amount) : ICommand;
```

## Wire `AuthorizingMediator`

`AddCommonFrameworkServices()` registers `AuthorizingMediator` as the default `IMediator` decorator. The marker interface `IAuthorizingMediator` is what `AuthorizationStartupValidator` checks at host-start — if a custom decorator chain accidentally hides the authorizing mediator, the host fails-fast.

## Custom `IAuthorizationProvider`

Default behavior reads roles from `SessionContext.ActorRoles`. Override by registering your own:

```csharp
public sealed class FineGrainedAuthorizationProvider : IAuthorizationProvider
{
    public Task<bool> AuthorizeAsync(IReadOnlySet<string> requiredRoles, SessionContext session, CancellationToken ct)
    {
        // Custom logic — e.g. consult an external policy server
    }
}

services.AddSingleton<IAuthorizationProvider, FineGrainedAuthorizationProvider>();
```

## Why the boundary check is at the mediator (not the endpoint)

A common mistake: declaring `[Authorize]` on an ASP.NET endpoint *and assuming* that's the full security boundary. If a command can also be dispatched from a worker (e.g. via the outbox), the endpoint-level check doesn't fire.

Stratara puts the check at `IMediator.HandleAsync(…)` because **every** dispatch path goes through it — HTTP, gRPC, console, worker, saga. One enforcement point, every channel covered.

## Operational

- **`AuthorizationStartupValidator`** runs at host-start. It walks every registered handler + checks that the corresponding `[RequireRole]`-marked command is wired through `IAuthorizingMediator`. Misconfiguration → fail-fast at boot, not at first dispatch.
- **`AuthorizationStartupValidator.FindRoleProtectedTypes`** uses reflection. Add `[ExcludeFromCodeCoverage]` on `[RequireRole]`-marked records if your coverage report flags them; they're attribute-only types.
