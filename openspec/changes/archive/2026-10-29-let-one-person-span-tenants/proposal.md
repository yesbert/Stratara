# Let one person span tenants

> **Status:** approved

## Why

A host that serves one person across several tenants — and, sharing one identity store, across several
applications — needs four things the framework does not give it today. A membership can only be
deleted, so a tenant forgets that someone was ever a member. A person's own data has no documented key
of its own, so erasing one of their tenants takes their profile with it. An event type encrypted as a
whole becomes unreadable once its subject is erased, and the replay that meets it stops there. And a
tenant stamped at sign-in is never checked again, so a person removed from a tenant keeps acting in it
until their sign-in is re-issued — while resolving the tenant on every request instead follows a
selection that every application sharing the store moves.

## What Changes

The consumer-visible effect, point by point:

- **A membership can end without being deleted.** `MembershipStatus` gains `Former`. A former
  membership confers nothing — like a pending one it satisfies no role, permission, cross-tenant or
  sign-in check — is never chosen as the tenant at sign-in, cannot be selected, and stays listed by
  the directory until the tenant's or the user's erasure sweeps it like any other row. Setting a
  membership to any status other than active clears a selection that pointed at it, as removing it
  already does.
- **What a replay reads after an erasure is guaranteed.** A field encrypted for an erased subject
  reads as absent, the event's other fields read normally, and the replay continues. The existing
  requirement said so for a revoked key; it now says so for an erasure and is tested through one.
- **BREAKING (behaviour): an event or aggregate type marked for encryption as a whole refuses to
  start the host.** Its payload or snapshot has no other fields to recover once the subject is
  erased: the event cannot be mapped and the snapshot cannot be restored, so the replay or the
  rebuild stops exactly where the erasure happened. Marking individual properties is the supported
  way. Commands and other registered types may still be marked as a whole.
- **A person's own data has an owner and a key of its own.** A stream can be created for a user
  alone — an explicit owner that names the user and no tenant, which the store refused until now. It
  keeps that owner for every later event, whatever tenant the appending session names, and its
  encrypted fields are keyed to the person alone: shredded by the person's erasure, untouched by the
  erasure of any tenant they belong to. The subject eraser now names that scope itself rather than
  relying on the key store being able to list its scopes — a key store that cannot list left it
  behind.
- **A tenant can be fixed at sign-in and still checked on every request.** A new registration keeps
  the tenant claim the sign-in stamped — across a re-issue of the sign-in as well — and on every
  request verifies that the person's membership in that tenant is still active. Where it is not, the
  claim is removed, so the request fails closed to the reserved default tenant exactly as one without
  a membership does. A tenant changes only when the host re-issues the sign-in after an explicit
  switch.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `tenant-directory`: *An invited membership confers nothing* becomes the rule for every membership
  that is not active and gains the former status; *A user has at most one selected tenant* clears a
  selection whose membership stops being active; *The tenant claim can be stamped at sign-in or
  resolved per request* gains the third mode — stamped, kept, and checked on every request; *A
  subject's erasure can be performed as one operation* names the person's own key scope.
- `event-sourcing-store`: *The owning tenant is resolved from the stream before the session* and *A
  new stream's owner can be stated at creation* accept an explicit owner that names a user alone, and
  a stream so created keeps it.
- `data-encryption`: *Unreadable encrypted fields degrade rather than fail* covers an erased subject
  and a replay; a new requirement refuses whole-type encryption of what is stored in a stream; *Keys
  are scoped, and a scope is the unit of erasure* states the key of a person alone and how data is
  written under it.

## Impact

- `src/Stratara.Abstractions/Abstractions/Multitenancy/MembershipStatus.cs` — `Former`;
  `TenantMembership.cs` and `ITenantMembershipStore.cs` documentation.
- `src/Stratara.Identity.EntityFrameworkCore/EfTenantMembershipStore.cs`,
  `src/Stratara.Testing/InMemoryTenantMembershipStore.cs` — a selection is cleared when its
  membership stops being active.
- `src/Stratara.Identity.AspNetCore/DependencyInjection/MembershipClaimsServiceCollectionExtensions.cs`,
  `src/Stratara.Identity.AspNetCore/Services/CheckedMembershipClaimsTransformation.cs`,
  `MembershipTenantClaimRefresh.cs`, `MembershipTenantClaimResolver.cs` — the kept-and-checked tenant claim.
- `src/Stratara.Infrastructure/Security/Serialization/EncryptionMetadataDriftGuard.cs`,
  `WholeTypeStreamEncryption.cs` (new) and `src/Stratara.Infrastructure/EventSourcing/EventSource.cs` — whole-type
  encryption of event and aggregate types refused at start-up and at append.
- `tests/Stratara.Infrastructure.Tests/EventSourcing/UnhandledEventRehydrationTests.cs` — its event, marked as a
  whole, now marks its property.
- `src/Stratara.Abstractions/Abstractions/EventSourcing/EventSubject.cs` (`ForUser`, `NamesAnOwner`),
  `IEventSource.cs`, `src/Stratara.Infrastructure/EventSourcing/EventSource.cs` — an owner that names a
  user alone is accepted and kept.
- `src/Stratara.Infrastructure/Security/SubjectEraser.cs` — the person's own scope under the reserved
  default tenant.
- `src/Stratara.Diagnostics/LogEvents.cs` — event ids for a removed stale claim, if logged.
- `docs/guides/tenant-membership.md`, `docs/guides/encrypt-data-setup.md`,
  `docs/reference/di-extensions-cheatsheet.md`, the
  `Stratara.Identity.AspNetCore` README — former members, the three claim modes and which to choose
  when applications share one identity store, how to write a person's own data, nullable types for
  erasable fields.
- `llms-full.txt` — the reference catalogue lists the new registration and status.
- `CHANGELOG.md`. A behaviour change, a new enum value and a new registration: a minor release.
