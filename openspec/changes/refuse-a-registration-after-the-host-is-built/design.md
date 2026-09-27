## Context

The helpers in question share one shape. Each looks for a descriptor whose `ImplementationInstance` is its own type:

- `GetOrAddCatalog<TCatalog>` (permission and setting catalogs)
- `AddMembershipAuthorizationOptions`
- `TrustedTypeResolverServiceCollectionExtensions.GetOrAddResolver`
- `RolePlacement.Publish`
- `SingletonWorkRegistrations.Of`

Each then returns that instance, or configures it, and registers a new one only when it finds none. After
`HostApplicationBuilder.Build()` (and `WebApplicationBuilder.Build()`) the collection is read-only. `Add` throws
there, but the found-instance path never calls `Add`.

## Goals / Non-Goals

**Goals:** every one of these helpers refuses on a read-only collection before it touches anything, with one wording.

**Non-Goals:**

- A provider built with a bare `BuildServiceProvider()` leaves the collection writable, so nothing can tell that a
  provider exists. That case is unchanged, and it is the consumer's own composition.
- Registrations that only add descriptors already fail on a read-only collection, with .NET's own error.

## Decisions

- **Check `services.IsReadOnly` at the top of each helper.** `IServiceCollection` is an
  `IList<ServiceDescriptor>`, and `ServiceCollection` reports `IsReadOnly` once `MakeReadOnly()` has run, which is
  what the host builders call. The helpers live in four assemblies across two tiers, and the check is one line
  plus a message. A shared helper would need a new public API in `Stratara.Abstractions`, so each site throws its
  own `InvalidOperationException`, worded the same. *Evidence:* one test per helper that builds a
  `HostApplicationBuilder`, calls the registration afterwards, and asserts the exception and the unchanged instance.
- **The message names the public registration, not the helper.** For example: "AddPermissionCatalog was called
  after the host was built: the service collection is read-only, and the catalog the running host reads would change
  under it. Call AddPermissionCatalog while the host is being configured." `GetOrAddResolver` and the Orleans
  helpers take the caller's name where they have one. Where they have none, they name the thing that would change:
  the trusted types, the roles, the singleton works.
- **S4136:** move `AddMembershipAuthorizationOptions` below the two `AddMembershipAuthorization<TUser>` overloads.
  This is a pure move, verified by the diff and by the nightly analysis.

## Risks / Trade-offs

- [A consumer registers after `Build()` today and relies on the silent change] → It never reliably worked: the
  instance may already have been read, and the declaration raced its readers. The failure now names the fix. It is
  listed under *Fixed* in the release notes.
