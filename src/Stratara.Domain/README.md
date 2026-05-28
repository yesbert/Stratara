# Stratara.Domain

> **License:** [FSL-1.1-MIT](../../LICENSE) (Functional Source License — source-available; converts to MIT after 2 years). Not OSI-approved OSS.

The Stratara framework's concrete multitenancy domain — the `Tenant` aggregate and its event vocabulary. Use this when your application wants Stratara's opinionated tenant model (one tenant per customer, lifecycle events for activate / deactivate / rename / locale-change / assignment) and the corresponding aggregate.

## Contents

- `Stratara.Domain.Multitenancy.Tenant` — the aggregate. Implements `IAggregate` (from `Stratara.Abstractions`).
- `Stratara.Domain.TenantCreated` / `TenantRenamed` / `TenantActivated` / `TenantDeactivated` / `TenantDefaultLocaleChanged` / `TenantAssignedToCustomer` / `TenantDeleted` / `CustomerTenantsDeleted` — the event records consumed by the aggregate's `Apply()` methods + persisted to the event stream.

## When to skip this package

If you're building a Stratara-on-Mediator application without the framework's tenant model (e.g. you have your own tenancy concept), reference `Stratara.Abstractions` alone for the marker interfaces. Most Stratara features (CQRS, event sourcing, projections, sagas) don't depend on `Stratara.Domain`.

## Quick reference

```csharp
// Open a Tenant stream from a command handler
await events.CreateAsync<Tenant>(tenantId,
    new TenantCreated(
        Id: tenantId,
        CustomerId: customerId,
        Name: "Acme",
        DefaultLocale: "de-DE",
        IsActive: true,
        CreatedAt: DateTimeOffset.UtcNow),
    cancellationToken);
await events.SaveChangesAsync(cancellationToken);

// Later: rehydrate the aggregate
var tenant = await aggregator.AggregateAsync<Tenant>(tenantId, cancellationToken: cancellationToken);
```

## Dependencies

- `Stratara.Abstractions` — for `IAggregate` (Tenant implements it).
- `JetBrains.Annotations` — for static-analysis attributes.
