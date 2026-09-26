# Stratara.Domain

> **Derived.** The behaviour described here is specified under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them.

> **License:** [MIT](../../LICENSE).

The Stratara framework's concrete multitenancy domain — the `Tenant` aggregate and its event vocabulary. Use this when your application wants Stratara's opinionated tenant model (one tenant per customer, lifecycle events for activate / deactivate / rename / locale-change / assignment) and the corresponding aggregate.

## Contents

- `Stratara.Domain.Multitenancy.Tenant` — the aggregate. Implements `IAggregate` (from `Stratara.Abstractions`).
- `Stratara.Domain.TenantCreated` / `TenantRenamed` / `TenantActivated` / `TenantDeactivated` / `TenantDefaultLocaleChanged` / `TenantAssignedToCustomer` / `TenantDeleted` — the event records consumed by the aggregate's `Apply()` methods + persisted to the event stream.
- `Stratara.Domain.CustomerTenantsDeleted` — the cascade event that deletes all of a customer's tenants at once. You append it to your own customer aggregate's stream; `TenantProjection` removes the listed tenants from the read model. No framework aggregate applies it, and your customer aggregate needs no `Apply()` for it: a rebuild skips an event the aggregate has no `Apply()` for without reading it. In a host that never registers the type, the first skip per process logs a warning; register it there with `AddTrustedType<CustomerTenantsDeleted>()` to acknowledge it. (An aggregate with an `Apply()` taking an interface, an abstract class or `object` reads every unresolvable event instead — see the snapshot guide.) It does not touch the tenants' own streams, so a `Tenant` rehydrated afterwards reads as deleted only if `TenantDeleted` was appended to its stream as well.

`TenantCreated` implements `IAggregateCreationEvent`, declaring the tenant it creates as the event's
own data owner. A tenant therefore belongs to itself no matter which session performed the creation —
an operator creating a tenant does not end up owning it. The interface is a compile-time contract read
when the event is appended; it is not serialized, so the record's JSON is unchanged.

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
