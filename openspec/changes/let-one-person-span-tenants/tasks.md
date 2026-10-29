## 1. Reproduce first

- [ ] 1.1 `tests/Stratara.Infrastructure.Tests/EventSourcing/`: a stream holds an event whose payload
  type is marked as a whole and an event with one marked property, both for a user who is then erased
  through `SubjectEraser`. Confirm on `main` that rebuilding the aggregate throws
  `InvalidOperationException("Event data could not be deserialized.")` — the evidence — and that with
  only the property-marked event the rebuild completes with the field `null`. Keep the second half as
  the regression test for *A stream is replayed across an erasure*.
- [ ] 1.2 `tests/Stratara.Infrastructure.Tests/Security/SubjectEraserKeyCoverageTests.cs`: a value is
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

- [ ] 3.1 `tests/Stratara.Infrastructure.Tests/Security/SecureJsonSerializerTests.cs`: property-level
  field after the subject's erasure (via `MarkSubjectErasedAsync` + `EraseScopeAsync` on
  `InMemoryKeyStore`) reads `null`, siblings intact; a non-nullable value-type field reads default and
  the nullable twin reads `null`.
- [ ] 3.2 Move the `Apply`-parameter discovery out of
  `src/Stratara.Abstractions/Abstractions/Reflections/TrustedTypeResolverServiceCollectionExtensions.cs`
  into one helper both the registration and the guard use; existing registration tests stay green.
- [ ] 3.3 `src/Stratara.Infrastructure/Security/Serialization/EncryptionMetadataDriftGuard.cs`: refuse
  every registered `IAggregate` and every event payload type its `Apply` methods take whose
  `EncryptionMetadata.ClassAttribute` is set; collect all, one exception naming each type and the
  remedy. Tests in `EncryptionMetadataDriftGuardTests`: event marked as a whole fails; aggregate
  marked as a whole fails; property-marked event starts; command marked as a whole starts.
- [ ] 3.4 `src/Stratara.Infrastructure/EventSourcing/EventSource.cs`: before serialising an event,
  refuse a payload type whose metadata has a class attribute, with the same message; nothing is
  written. Test in `EventSourceTests`: an `AddTrustedType`-only event marked as a whole fails the
  append and the stream is unchanged.
- [ ] 3.5 Test that a type whose marking moved from the class to a property still reads a payload
  written as a whole (shape detection in `SecureJsonSerializer.DeserializeAsync`), in
  `SecureJsonSerializerTests`.
- [ ] 3.6 Replay through a projection: the 1.1 scenario through the projection test host, every event
  delivered in order, the replay completes. Name the test in the task once written.

## 4. A key of the person

- [ ] 4.1 `src/Stratara.Infrastructure/Security/SubjectEraser.cs`: the user's directory scopes include
  `(UserScoped, Format(Guid.Empty), user)`. 1.2 inverted.
- [ ] 4.2 `tests/Stratara.Infrastructure.Tests/EventSourcing/`: a stream created under a session with
  the reserved default tenant and the person as data-owner user, appended to under a session of tenant
  A; the payload is encrypted under the default-tenant scope (key-store inspection), reads under a
  session of tenant A and B, survives tenant A's erasure, and reads absent after the user's erasure.

## 5. Tenant fixed at sign-in, checked on every request

- [ ] 5.1 `src/Stratara.Identity.AspNetCore/Services/`: the checking `IClaimsTransformation` — claim
  present and membership `Active` → unchanged; not `Active` or missing → clone without the claim;
  no claim → unchanged, never resolved live.
- [ ] 5.2 The `SecurityStampValidatorOptions.OnRefreshingPrincipal` hook: copies the current tenant
  claim onto the new principal when still `Active`, removes it otherwise, chains to a handler already
  set.
- [ ] 5.3 `src/Stratara.Identity.AspNetCore/DependencyInjection/MembershipClaimsServiceCollectionExtensions.cs`:
  the new registration (factory decorator + hook + transformation); throws `InvalidOperationException`
  naming both when combined with `AddMembershipTenantClaimsTransformation()` in either order. XML docs
  state when membership and selection changes take effect in all three modes.
- [ ] 5.4 Tests in `MembershipClaimsBridgeTests` (or a sibling): another session moves the selection →
  claim kept, including through a cookie refresh driven by `SecurityStampValidator`; host sets the
  selection and re-issues → new tenant; membership set to `Former` → next request has no claim and no
  substitute; refresh after the membership ended → no claim; both registrations → throws.

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
