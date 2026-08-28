# Authorization Decorators

> **Derived page.** The behaviour described here is specified by the `authorization` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

Stratara enforces role-based authorization at the mediator boundary via `[RequireRole]` and the
authorizing mediator — so every command and query crosses a single mandatory check, regardless of
which channel delivered it (HTTP, MAUI, console, worker). The attribute lives in
`Stratara.Abstractions.Authorization`, so a request type can declare its guard without depending on
whatever evaluates it.

Roles are the coarse gate. For fine-grained checks, `[RequirePermission]` composes on the same
boundary — see [Permission-Based Authorization](require-permission.md).

## Mark a command

```csharp
using Stratara.Abstractions.Authorization;

[RequireRole("BankTeller")]
public sealed record DepositCommand(Guid AccountId, decimal Amount) : ICommand;
```

If the registered `IAuthorizationProvider` doesn't confirm the role, the authorizing mediator throws
`AuthorizationException` — whose `RequiredRole` names the missing role — **before** the handler is
resolved.

## Multiple roles are ANDed

`RequireRoleAttribute` takes exactly one role and is `AllowMultiple`, so requiring several means
stacking the attribute. **Every** listed role must be held:

```csharp
[RequireRole("BankTeller")]
[RequireRole("Supervisor")]   // must have both
public sealed record HighValueTransferCommand(Guid From, Guid To, decimal Amount) : ICommand;
```

There is no built-in "any one of these roles" form. For an either/or rule, model it as a permission
and grant that permission to both roles — the catalog exists for exactly this.

## Wire the authorizing mediator

The decorator is opt-in. Register it with the provider that answers role checks:

```csharp
// The membership-backed provider the framework ships (Stratara.Identity.EntityFrameworkCore):
services.AddAuthorizingMediator<MembershipAuthorizationProvider>();
```

`AddAuthorizingMediator<T>()` registers the provider, wraps the inner mediator, and picks up an
`IPermissionResolver` if one is registered. **Composition helpers like
`AddCommonFrameworkServices()` do not wire this for you** — without this call, `[RequireRole]` is
inert.

That is what `IAuthorizingMediator` (`Stratara.Abstractions.Mediator`) and the startup validator
exist to catch: if guarded request types are registered while the resolved `IMediator` isn't an
authorizing one, the host fails at boot rather than serving unguarded requests.

## Custom `IAuthorizationProvider`

The contract is deliberately narrow — one role, one answer:

<!-- stratara-snippet-ignore: names a policy server the consumer supplies -->
```csharp
public sealed class FineGrainedAuthorizationProvider(ISessionContextProvider sessions)
    : IAuthorizationProvider
{
    public async Task<bool> IsInRoleAsync(string role, CancellationToken cancellationToken = default)
    {
        var session = sessions.Current;
        if (session is null)
        {
            return false;   // fail closed — no session, no roles
        }

        // e.g. consult an external policy server for session.ActorUserId
        return await PolicyServer.HasRoleAsync(session.ActorUserId, role, cancellationToken);
    }
}

services.AddAuthorizingMediator<FineGrainedAuthorizationProvider>();
```

The provider resolves roles itself — `SessionContext` carries identity (`ActorUserId`, `TenantId`),
**not** a role list. Roles are never embedded in the session, which is why revoking one takes effect
on the next dispatch instead of when a token expires.

## Why the check sits at the mediator, not the endpoint

A common mistake is declaring `[Authorize]` on an ASP.NET endpoint and assuming that is the security
boundary. If the same command can also arrive from a worker draining the outbox, the endpoint check
never fires.

Stratara puts the check at `IMediator.HandleAsync(…)` because **every** dispatch path crosses it —
HTTP, gRPC, console, worker, saga. One enforcement point, every channel covered. The authorizing
outbox dispatcher (`Stratara.Infrastructure`) applies the same attributes on the way *into* the
outbox, so an async command is guarded at enqueue time too.

Both decorators read the attributes off the request's **runtime** type, so a command dispatched
through a base-typed variable still gets the derived type's guards.

## See also

- [Permission-Based Authorization](require-permission.md) — the fine-grained sibling and its catalog.
- [Tenant Membership](tenant-membership.md) — where `MembershipAuthorizationProvider` gets its roles.
- [Enforce Tenant Isolation](enforce-tenant-isolation.md) — the orthogonal guard on *which tenant's*
  data a request may touch.
