# Design — Let a store context bring its unit of work

## Context

See `proposal.md` → *Why*. What matters here is what the two registrations do today and what the
one place that does register the unit of work looks like.

`NpgsqlDbContextServiceCollectionExtensions` (`src/Stratara.EventSourcing.EntityFrameworkCore/EntityFrameworkCore/DependencyInjection/`):

- `AddNpgsqlWriteDbContextFactory<TDbContext>()` (line 34) — `AddDbContextFactory<TDbContext>` scoped,
  `TryAddScoped<IWriteDbContext>` from the factory, `TryAddScoped<IDbResolver, DefaultDbResolver>`.
- `AddNpgsqlReadDbContextFactory<TDbContext>()` (line 54) — the factory and the resolver only.

`WriteUnitOfWork<TDbContext>` (`WriteStore/WriteUnitOfWork.cs:24`) is constructed from
`(IDbContextFactory<TDbContext>, ISessionContextProvider, ISecureJsonSerializer)`; the last two come
from `AddSessionContext()` and `AddSecurity()`, which every composite applies.
`ProjectionsUnitOfWork<TDbContext>` (`ReadStore/ProjectionsUnitOfWork.cs:16`) takes only the
factory and implements `IProjectionsUnitOfWork : IReadUnitOfWork`.

The only registration of `IWriteUnitOfWork` for a concrete context in the family is in test support,
`AddStrataraTestingEventStore` (`src/Stratara.Testing.EntityFrameworkCore/TestEventStoreServiceCollectionExtensions.cs:72`):

```csharp
services.AddScoped<IWriteUnitOfWork>(sp => new WriteUnitOfWork<TWriteDbContext>(
    sp.GetRequiredService<IDbContextFactory<TWriteDbContext>>(),
    sp.GetRequiredService<ISessionContextProvider>(),
    sp.GetRequiredService<ISecureJsonSerializer>()));
```

`docs/reference/di-extensions-cheatsheet.md:123` describes the write factory as registering "the
default `IWriteUnitOfWork` if none is registered". `llms-full.txt` is generated from the XML docs
and the documentation tests fail when it drifts from them.

## Goals / Non-Goals

**Goals:**

- Registering a context is sufficient for the store on that side to work.
- A consumer's own unit of work, registered before or after the context, wins.
- The unit of work is scoped, as the test host registers it and as the repositories it mints assume.

**Non-Goals:**

- Registering the unit of work from the composites (`AddBackendServices`, `AddCommandWorkerServices`).
  They do not know the consumer's context type, and the point is that the one call that does know it
  is the one that registers it.
- Changing the identity-context registration. ASP.NET Identity resolves the context itself; there
  is no unit of work on that side.
- Touching the SQLite test host. It keeps its explicit registration, which is now redundant with
  the production one but harmless, and its `AddScoped` (not try-add) is deliberate there.

## Decisions

### The factory registration try-adds the unit of work for its own context type

`AddNpgsqlWriteDbContextFactory<TDbContext>()` adds
`TryAddScoped<IWriteUnitOfWork>(sp => new WriteUnitOfWork<TDbContext>(...))` with the three
constructor dependencies resolved from the provider, exactly the shape the test host uses.
`AddNpgsqlReadDbContextFactory<TDbContext>()` adds
`TryAddScoped<IProjectionsUnitOfWork>(sp => new ProjectionsUnitOfWork<TDbContext>(factory))` and
`TryAddScoped<IReadUnitOfWork>(sp => sp.GetRequiredService<IProjectionsUnitOfWork>())`, so the two
contracts resolve to one instance per scope.

Try-add is what makes precedence hold in both orders: a consumer registration before the factory
call means the framework's try-add is a no-op; a consumer registration after it is a later descriptor
for the same service type, which the container prefers. Registering the same context twice is a
second no-op.

Evidence: the constructor shapes above; the test host's registration; the new tests in
`tests/Stratara.EntityFrameworkCore.Tests/` that resolve each contract against a registered context
and assert the type and the precedence.

*Rejected: `AddScoped` rather than try-add.* It would silently replace a consumer's own unit of work
registered before the factory call — the opposite of what an additive patch may do.

*Rejected: a separate `AddNpgsqlWriteUnitOfWork<TDbContext>()` the consumer calls in addition.* A
second call that is mandatory for the first to be useful is the defect restated with one more name.

### The factory dependencies are resolved lazily, not captured at registration

The write unit of work needs the session provider and the serializer. Both are registered by
composites the consumer may call after the factory extension. Resolving them inside the factory
delegate, per scope, means the order of `Add*` calls does not matter, which is the standing rule of
the composition surface.

## Risks / Trade-offs

- [A consumer registered `IWriteUnitOfWork` as a singleton by hand, and now also gets a scoped
  try-add] → Try-add sees the existing descriptor and does nothing; the consumer's singleton stands.
  No change.
- [A host registers the write context but not `AddSecurity()` / `AddSessionContext()`] → The unit of
  work fails to resolve its serializer or session provider at first use, with the container's message
  naming that type. That host could not have run the store before either; the error now names a
  documented composite dependency rather than the unit of work itself.
- [`llms-full.txt` drifts] → It is regenerated as part of the change; the documentation tests are the
  gate.
