## 1. Reproduce first

- [x] 1.1 Whole-type gap: confirmed in the implementation (`EventMapperFactory.MapToEventAsync` turns a `null`
  payload into "Event data could not be deserialized.") and by
  `UnhandledEventRehydrationTests.An_unhandled_event_whose_key_is_gone_does_not_stop_the_rebuild`, whose event was
  marked as a whole and now marks its property. The property-level half is the regression test
  `StreamEncryptionAcrossErasureTests.A_stream_rebuilt_after_its_users_erasure_delivers_every_event_with_the_erased_fields_absent`.
- [x] 1.2 `tests/Stratara.Infrastructure.Tests/Security/SubjectEraserKeyCoverageTests.cs`: a value is
  encrypted under `UserScoped / Guid.Empty / user`, the user is erased through a key store that throws
  `NotSupportedException` from `ListScopesAsync`. Confirm on `main` the key survives; invert after 4.1.
- [x] 1.3 `tests/Stratara.Identity.EntityFrameworkCore.Tests/EfTenantMembershipStoreTests.cs`: a
  selected membership is rewritten with status `Pending`; confirm on `main` that
  `GetActiveTenantAsync` still returns it; invert after 2.2.

## 2. Former membership

- [x] 2.1 `src/Stratara.Abstractions/Abstractions/Multitenancy/MembershipStatus.cs`: `Former = 2`,
  documented as recording a past member that confers nothing; the type remarks name both non-active
  states. `TenantMembership.cs` and `ITenantMembershipStore.cs` documentation say list views return
  every status.
- [x] 2.2 `src/Stratara.Identity.EntityFrameworkCore/EfTenantMembershipStore.cs` and
  `src/Stratara.Testing/InMemoryTenantMembershipStore.cs`: `SetMembershipAsync` deletes the selection
  for `(user, tenant)` when the written status is not `Active`. Tests in `EfTenantMembershipStoreTests`
  and `tests/Stratara.Testing.Tests/InMemoryTenantMembershipStoreTests.cs`: set to `Former` and to
  `Pending` clears the selection; `SetActiveTenantAsync` on a former membership is refused; a former
  membership is listed by `GetMembersAsync` and `GetMembershipsAsync`.
- [x] 2.3 A former membership confers nothing, one test per check:
  `MembershipAuthorizationProviderTests` (role), `CatalogPermissionResolverTests` (permission),
  `MembershipCrossTenantAuthorizerTests` (cross-tenant), `EfApiKeyStoreTests` (machine-key
  materialisation), `tests/Stratara.Identity.AspNetCore.Tests/MembershipClaimsBridgeTests.cs`
  (not chosen at sign-in, a selection pointing at it yields no claim).
- [x] 2.4 `tests/Stratara.Infrastructure.Tests/Security/SubjectEraserTests.cs`: a user's and a tenant's
  erasure removes former memberships.

## 3. Reading after an erasure, and whole-type encryption in a stream

- [x] 3.1 `tests/Stratara.Infrastructure.Tests/Security/SecureJsonSerializerTests.cs`: property-level
  field after the subject's erasure (via `MarkSubjectErasedAsync` + `EraseScopeAsync` on
  `InMemoryKeyStore`) reads `null`, siblings intact; a non-nullable value-type field reads default and
  the nullable twin reads `null`.
- [x] 3.2 The guard reads the `Apply` parameters of each registered aggregate itself (design: no new public
  member in Tier-A for it); existing registration tests stay green.
- [x] 3.3 `src/Stratara.Infrastructure/Security/Serialization/EncryptionMetadataDriftGuard.cs`: refuse
  every registered `IAggregate` and every event payload type its `Apply` methods take whose
  `EncryptionMetadata.ClassAttribute` is set; collect all, one exception naming each type and the
  remedy. Tests in `EncryptionMetadataDriftGuardTests`: event marked as a whole fails; aggregate
  marked as a whole fails; property-marked event starts; command marked as a whole starts.
- [x] 3.4 `src/Stratara.Infrastructure/EventSourcing/EventSource.cs`: before serialising an event,
  refuse a payload type whose metadata has a class attribute, with the same message; nothing is
  written. Test in `EventSourceTests`: an `AddTrustedType`-only event marked as a whole fails the
  append and the stream is unchanged.
- [x] 3.5 Test that a type whose marking moved from the class to a property still reads a payload
  written as a whole (shape detection in `SecureJsonSerializer.DeserializeAsync`), in
  `SecureJsonSerializerTests`.
- [x] 3.6 Replay through a projection:
  `tests/Stratara.Projections.Tests/Services/ProjectionReplayAcrossErasureTests.cs` — every event delivered, the
  erased user's field `null`, the other user's intact, the replay completes without failure.

## 4. A key of the person

- [x] 4.1 `src/Stratara.Infrastructure/Security/SubjectEraser.cs`: the user's directory scopes include
  `(UserScoped, Format(Guid.Empty), user)`. 1.2 inverted.
- [x] 4.2 `tests/Stratara.Infrastructure.Tests/EventSourcing/StreamEncryptionAcrossErasureTests.cs`: a
  stream created with `EventSubject.ForUser(person)` under a session of tenant A, appended to under the
  same session; the entries carry `Guid.Empty` and the person, the payload is keyed to the default-tenant
  scope (key-store listing), reads under a session of tenant B, survives tenant A's erasure, and reads
  absent after the user's erasure.
- [x] 4.3 `EventSubject.ForUser` / `NamesAnOwner`; `EventSource` accepts a user-alone subject and its
  owner lookup keeps it; `IEventSource.CreateOnBehalfOfAsync` default the same. Tests in
  `EventSourceOwningUserTests`: a user-alone stream keeps its owner under a tenant's session;
  `ForUser(Guid.Empty)` throws; the existing empty-subject refusals in `EventSourceTests` stay green.

## 5. Tenant fixed at sign-in, checked on every request

- [x] 5.1 `src/Stratara.Identity.AspNetCore/Services/`: the checking `IClaimsTransformation` — claim
  present and membership `Active` → unchanged; not `Active` or missing → clone without the claim;
  no claim → unchanged, never resolved live.
- [x] 5.2 The `SecurityStampValidatorOptions.OnRefreshingPrincipal` hook: copies the current tenant
  claim onto the new principal when still `Active`, removes it otherwise, chains to a handler already
  set.
- [x] 5.3 `src/Stratara.Identity.AspNetCore/DependencyInjection/MembershipClaimsServiceCollectionExtensions.cs`:
  the new registration (factory decorator + hook + transformation); throws `InvalidOperationException`
  naming both when combined with `AddMembershipTenantClaimsTransformation()` in either order. XML docs
  state when membership and selection changes take effect in all three modes.
- [x] 5.4 `tests/Stratara.Identity.AspNetCore.Tests/CheckedMembershipTenantClaimTests.cs`: a selection moved
  elsewhere moves neither the request nor the cookie refresh (driven through the installed
  `SecurityStampValidatorOptions.OnRefreshingPrincipal`, which the validator calls); a re-issue after the host
  selects switches it; `Former`/`Pending`/removed membership → next request has no claim and no substitute, the
  original principal untouched; refresh after the membership ended → no claim; no claim → never resolved live;
  unreadable user → dropped; both registrations → throws in either order; registering twice decorates once.

## 6. Documentation and release notes

- [ ] 6.1 `docs/guides/tenant-membership.md`: former members; the three claim modes with when changes
  take effect and the recommendation for a shared identity store; a host's re-issue is the switch; the
  bearer-refresh limitation.
- [ ] 6.2 `docs/guides/encrypt-data-setup.md`: writing a person's own data (session with the reserved
  default tenant, a non-tenant-scoped command, the stream keeps its owner); the scope is fixed at
  write; nullable types for erasable fields; whole-type marking refused for events and aggregates and
  what that means for payloads already written.
- [ ] 6.3 `src/Stratara.Identity.AspNetCore/README.md`, `llms-full.txt`: the new registration and
  status.
- [ ] 6.4 `CHANGELOG.md`: Added (former status, kept-and-checked mode), Fixed (person's own scope in
  the erasure), Changed — breaking behaviour (whole-type encryption of event and aggregate types).
- [ ] 6.5 `./scripts/local-gauntlet.sh` green; `openspec validate let-one-person-span-tenants --strict`.
