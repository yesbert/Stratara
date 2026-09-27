# Refuse a registration after the host is built

> **Status:** approved

## Why

Several of the framework's registrations add to an instance already registered in the service collection instead
of registering a new one: the permission and setting catalogs, the membership authorization options, the
trusted-type resolver, and, under the Orleans execution model, the record of the roles a silo takes and the list of
singleton works. Called on `builder.Services` after the host was built, one of them that finds its instance changes
the live singleton the running host already reads. That is a data race on the instance's collections, and it is a
declaration that takes effect in the running host or not, depending on who read the instance first. The same call on
a collection without the instance fails with .NET's read-only error. The independent review of 4.3.1 found this for
the catalogs. The owner ruled on 2026-09-26 that 4.4.0 is not released while a known bug is open.

## What Changes

- Each framework registration that adds to an already registered instance refuses when the service collection is
  read-only, as a built host's is, with an `InvalidOperationException` that names the registration and says to
  call it before the host is built. A registration made while the host is being configured behaves as before.
- No behaviour change: the `AddMembershipAuthorization` overloads are placed next to each other in their class, which
  clears the code-analysis finding (rule S4136) that has kept the nightly quality gate red since 2026-09-24.

## Capabilities

### New Capabilities

### Modified Capabilities

- `host-composition`: an ADDED requirement. A framework registration does not change a service the running host
  already uses. Called on a service collection the host was built from, it fails and names itself.

## Impact

- `Stratara.Identity.EntityFrameworkCore`: `AddPermissionCatalog`, `AddSettingCatalog`,
  `AddMembershipAuthorizationOptions` and the `AddMembershipAuthorization` overloads that call it.
- `Stratara.Abstractions`: `TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver`, and with it every
  registration that declares trusted types: `AddTrustedType<T>`, `AddAggregatesFromAssemblyContaining<T>`,
  `AddDomainEventTypesFromAssemblyContaining<T>`, and the mediator, projection and saga discovery.
- `Stratara.Orleans`: `AddStrataraAggregateGrains`, `AddStrataraProjectionGrains`, `AddStrataraSagaGrains`,
  `AddStrataraDurableTimers` (role placement) and `AddStrataraSingletonWork`.
- Tests: `Stratara.Identity.EntityFrameworkCore.Tests`, the Abstractions resolver tests, `Stratara.Orleans.Tests`.
- `CHANGELOG.md` under the open `[4.4.0]`.
