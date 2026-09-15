---
title: "Session Context"
description: "The one answer to who is asking and whose data it is, carried from the request that authenticated it to the store that persists it: actor and data owner, fail-closed tenant resolution, trace tags and correlation."
---

# Session Context

> **Derived page.** The behaviour described here is specified by the `session-context` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

Every call in a Stratara application carries a session context. It holds one answer to two
questions: *who is asking*, and *whose data is this*. It follows the call from the transport that
authenticated it to the store that persists it. Routing, [tenant-aware encryption](tenant-aware-encryption.md),
query filtering, [tenant isolation](../guides/enforce-tenant-isolation.md) and audit stamping all
read that one answer, so they agree with each other and none of them works out identity for itself.

The value is the `SessionContext` record from `Stratara.Contracts`. You read and set it through
`ISessionContextProvider` from `Stratara.Abstractions`. The provider and the HTTP middleware ship in
`Stratara.Sessions`.

## Actor and data owner

A session context carries two separate identities:

- **The actor** is the principal who triggered the operation: `ActorTenantId` and `ActorUserId`.
  The audit trail follows the actor.
- **The data owner** (the subject) is the principal whose data the operation concerns: `TenantId`
  and `UserId`. Routing, the encryption scope and the query filter follow the data owner.

The naming rule has no exceptions. A tenant or user identifier with no prefix always means the data
owner, and an actor identifier always says *Actor*.

Most of the time the two are the same principal. A user who works on their own tenant's data is
both actor and data owner. They differ in three cases:

| Situation | Data owner | Actor |
|---|---|---|
| A privileged operation acts on a foreign tenant | the **target** tenant | the tenant of the principal who triggered it |
| An anonymous request | whatever the endpoint sets | the empty identifier (`Guid.Empty`) |
| A system or saga flow with no inherited actor | the tenant the flow works on | `SessionContext.SystemActorTenantId` and `SessionContext.SystemActorUserId` |

The system values are reserved sentinels. They are not `Guid.Empty`, so an audit record can tell a
system flow from an anonymous request.

For a privileged cross-tenant operation, the endpoint first authorizes the caller. It then promotes
the data-owner tenant to the target and leaves the actor unchanged. After that, everything derived
from the data owner follows the target tenant, and the audit record still names the admin who did
the work:

```csharp
using Stratara.Abstractions.Session;

public sealed class ForeignTenantScope(ISessionContextProvider sessions)
{
    // Call only after the caller has been authorized to act on targetTenantId.
    public void ActOn(Guid targetTenantId)
    {
        var current = sessions.Current
            ?? throw new InvalidOperationException("No session context is set.");

        sessions.Set(current with { TenantId = targetTenantId });
    }
}
```

In strict mode, [Enforce Tenant Isolation](../guides/enforce-tenant-isolation.md) sends exactly this
case (actor tenant differs from data-owner tenant) through an authorizer.

## The connection identity

A session context also carries a third identity that belongs to neither the actor nor the data
owner. `ClientId` names the *connection* the operation arrived on: a browser tab, a phone call, or a
server-rendered session. Use it when you need to address that one connection. It is optional,
because not every operation has a connection behind it.

On an HTTP request, the value comes from the `X-Client-Id` header (`StrataraHeaderNames.ClientId`):

- A header with a parsable identifier puts that identifier on the context as `ClientId`.
- If the header is absent, empty or not parsable, `ClientId` is `null`. Nothing gets a default
  value, and the request is not rejected because of the header.

## The context is ambient and settable

Any component in the call can read the current context from `ISessionContextProvider.Current`. You
don't pass it through method signatures. The provider is registered per scope, so within one HTTP
request, one worker message or one saga step, every component that resolves it reads the same
context.

- **Before anything sets it**, `Current` is `null`. The provider never makes up a context.
- **`Set(context)`** replaces the value, and every later read sees the new context. The
  promotion above is an example: a second `Set` replaces the first.
- **`Clear()`** removes the value at the end of a unit of work, and later reads see `null` again.

A flow that doesn't start from a request sets its own context. It names the system as the actor and
clears the context when it is done:

```csharp
using Stratara.Abstractions.Session;
using Stratara.Contracts.Session;

public sealed class NightlyInvoiceRun(ISessionContextProvider sessions)
{
    public Task RunForTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        sessions.Set(new SessionContext(
            CorrelationId: Guid.CreateVersion7().ToString("N"),
            CausationId: null,
            ClientConnectionId: null,
            ActorTenantId: SessionContext.SystemActorTenantId,
            ActorUserId: SessionContext.SystemActorUserId,
            TenantId: tenantId,
            UserId: null));

        try
        {
            // Work that reads the ambient context: repositories, encryption, audit.
            return Task.CompletedTask;
        }
        finally
        {
            sessions.Clear();
        }
    }
}
```

Register the provider with `services.AddSessionContext()`. The worker composite
`AddCommonFrameworkServices()` from `Stratara.EventSourcing.WorkerDefaults` already calls it. The
framework's workers set the context themselves for each message they process. In tests,
`TestSessionContextProvider` from `Stratara.Testing` gives you a preset context (see
[Testing Patterns](../guides/testing-patterns.md)).

## An authenticated HTTP request populates the context from its claims

`SessionContextMiddleware` fills in the context from the request's principal, so it is set before
the request reaches your endpoints. Place the middleware **after** authentication, because it only
works with a principal that has already been authenticated:

```csharp
builder.Services.AddSessionContext();
builder.Services.AddOptions<SessionContextOptions>()
    .Bind(builder.Configuration.GetSection(SessionContextOptions.SectionName));

var app = builder.Build();

app.UseAuthentication();
app.UseMiddleware<SessionContextMiddleware>();
```

What happens depends on the request:

- **Authenticated principal.** The middleware sets a context for the request:
  - The user identity comes from the principal's name-identifier claim (`ClaimTypes.NameIdentifier`).
  - The tenant comes from the `stratara:tenant_id` claim (`StrataraClaimTypes.TenantId`), resolved
    as described in the next section.
  - The actor defaults to the data owner: `ActorTenantId` and `TenantId` are the same tenant, and
    the user from the claim is `ActorUserId`.
- **Principal not authenticated.** No context is set, and the request continues. `Current` stays
  `null`.
- **No name-identifier claim, or one that isn't a parsable identifier.** The user identity is the
  empty identifier (`Guid.Empty`). The request is not rejected.

To act on another tenant's data, promote the data-owner tenant after authorization, as shown in
[Actor and data owner](#actor-and-data-owner).

## Tenant resolution fails closed

An authenticated principal must not be able to pick the tenant its request runs against. When the
tenant claim is missing or can't be parsed, the data-owner tenant becomes a reserved default
identifier, `DefaultTenantIdentifier.Value`. A tenant sent by the caller is not accepted unless the
host has opted in.

| Principal's `stratara:tenant_id` claim | Header fallback | Data-owner tenant |
|---|---|---|
| present and parsable | either setting | the claim; the header is not read |
| missing or unparsable | off (the default) | `DefaultTenantIdentifier.Value`; any `X-Tenant-Id` header is ignored |
| missing or unparsable | on | the `X-Tenant-Id` header if it is parsable, otherwise `DefaultTenantIdentifier.Value` |

The fallback is controlled by `SessionContextOptions`, which binds from the `SessionContext`
configuration section:

```json
{
  "SessionContext": {
    "AllowTenantHeader": false
  }
}
```

`AllowTenantHeader` defaults to `false`. `AddSessionContext()` registers the options but does not
bind them to configuration. To set the value from `appsettings.json`, bind the section yourself, as
the pipeline example above does, or set it in code with
`services.Configure<SessionContextOptions>(o => o.AllowTenantHeader = true)`.

Turn the fallback on only in two cases: an upstream check (such as a platform-admin role gate)
controls who can send the header, or the header is part of a trusted service-to-service contract.
Where you can, put the tenant into the principal's claims instead, and the fallback is never
needed.

## Setting the context stamps the current trace

When a context is set, the provider tags the active trace span with four values. When the context
is cleared, it removes those tags. Traces can then be filtered by tenant or by the principal who
triggered the work, and no component has to emit those values itself.

| Tag | Value | Constant |
|---|---|---|
| `correlation.id` | the correlation identity | `ApplicationDiagnostics.CorrelationIdTagName` |
| `causation.id` | the causation identity | `ApplicationDiagnostics.CausationIdTagName` |
| `tenant.id` | the **data-owner** tenant | `ApplicationDiagnostics.TenantIdTagName` |
| `user.id` | the **actor** user | `ApplicationDiagnostics.UserIdTagName` |

The tenant tag is the data owner, and the user tag is the actor. A span for a privileged
cross-tenant operation therefore shows the tenant whose data was touched and the admin who touched
it. If no span is active, setting and clearing the context still work, and nothing is tagged.

## Correlation survives a request that supplies none

Every context the middleware sets has a correlation identity, so every unit of work can be traced:

- If the request has a non-empty trace identifier (`HttpContext.TraceIdentifier`), that identifier
  becomes `CorrelationId`.
- If the trace identifier is empty, the middleware generates a new time-ordered identifier (a
  version 7 GUID).

## The ambient identities are readable without the context

Some components need only one identity. A query filter needs the tenant, and an audit stamp needs
the user. They don't have to depend on the whole context shape. Two small services from
`Stratara.Abstractions.Multitenancy` return each value on its own:

- **`ITenantService.GetTenantId()`** returns the **data-owner** tenant, not the actor's tenant.
- **`ICurrentUserService.GetId()`** returns the **actor** user, not the data owner's user.

If no context is set, both return the empty identifier (`Guid.Empty`) instead of throwing. Callers
never have to handle a missing value.

```csharp
using Stratara.Abstractions.Multitenancy;

public sealed class InvoiceStamp(ITenantService tenants, ICurrentUserService currentUser)
{
    public (Guid OwnerTenantId, Guid CreatedByUserId) Stamp() =>
        (tenants.GetTenantId(), currentUser.GetId());
}
```

Register both with `services.AddIdentity()` from `Stratara.Infrastructure`, next to
`AddSessionContext()`. The worker composite registers both.

## See also

- **[Tenant-Aware Encryption](tenant-aware-encryption.md)**: the data-owner tenant as the
  cryptographic boundary.
- **[Enforce Tenant Isolation](../guides/enforce-tenant-isolation.md)**: the entrance guard that
  compares a request's tenant with the data owner, and strict mode for the cross-tenant case.
- **[Tenant Membership](../guides/tenant-membership.md)**: how the tenant claim this middleware reads
  gets into the principal.
- **[Actor vs Subject](../overview/glossary.md#actor-vs-subject)**: the short glossary entry.
