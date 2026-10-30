## Context

The four points of this change come from one host decision: a person has one account across several
applications sharing one identity store and across several tenants, and the applications are told
apart only by role-name prefixes. See proposal.md — Why. The state each point starts from:

**Membership status.** `MembershipStatus` has `Active` and `Pending`. Every place that grants
something compares for `== Active` — `MembershipRoleEvaluator`, both `CatalogPermissionResolver`s,
`MembershipCrossTenantAuthorizer`, `MembershipTenantClaimResolver`, `EfApiKeyStore`,
`EfTenantMembershipStore.SetActiveTenantAsync`, and the test-support doubles. A third value is
therefore refused everywhere without touching those call sites. The status column is a string of at
most 32 characters (`TenantMembershipEntryConfiguration`), so `Former` needs no schema change.
`SetMembershipAsync` replaces roles and status but leaves an `active_tenant` row alone;
`RemoveMembershipAsync` deletes both. Evidence: the implementation.

**Reading after an erasure.** `SecureJsonSerializer` asks `IKeyStore.GetDataEncryptionKeyAsync` by key
id when it decrypts, and that returns `null` for a revoked, an erased or an unknown id — so a field
encrypted under an erased scope already reads as absent (`null`, or the default of a non-nullable
value type) while its siblings read normally. A type encrypted as a whole goes through
`DecryptWholeJsonAsync`, which returns `null` for the whole object; `EventMapperFactory.MapToEventAsync`
and `MapToEventEnvelopesAsync` turn that into `InvalidOperationException("Event data could not be
deserialized.")`, and `AggregationService` into the "could not deserialize snapshot" failure that
*An unreadable snapshot fails loudly* requires. Evidence: the implementation;
`SecureJsonSerializerTests.Deserialize_RevokedKey_ReturnsNull`,
`Deserialize_PropertyLevel_RevokedKey_ReturnsNullProperties` and
`Deserialize_PropertyLevel_RevokedKeyOnAValueTypeField_ReadsAsDefault` cover revocation only.

**A key of the person.** Field encryption takes its scope from the owner recorded on the stream
(`EventSource` serialises under `subject.TenantId` / `subject.UserId`), and its associated data refuses
a `null` tenant (`BuildAdditionalAuthenticatedData`). A session without a tenant claim resolves to the
reserved default tenant, `Guid.Empty` (`SessionContextMiddleware`). A user's own data therefore lands
in scope `UserScoped / 00000000-0000-0000-0000-000000000000 / <user>`. `KeySubject.User` covers it, so
the erasure mark refuses it afterwards; a tenant's erasure never names it. `SubjectEraser` shreds the
user's directory scopes — `(UserScoped, null, user)` and one per tenant the user is a member of — plus
whatever `ListScopesAsync` reports. The default-tenant scope is not among the directory scopes, so a
key store that cannot list leaves it behind. Evidence: the implementation.

**The tenant claim.** `MembershipClaimsPrincipalFactory<TUser>` stamps the claim whenever a principal
is created from a user, resolving from the selection first. `MembershipClaimsTransformation` passes a
principal that already carries the claim through untouched and otherwise resolves live. Nothing ever
re-checks a stamped claim. ASP.NET Identity's security-stamp validation periodically re-creates the
cookie principal through the same factory, which resolves from the selection again — so in the
stamped mode a selection moved by another application reaches this sign-in at its next refresh.
Evidence: the implementation; the ASP.NET Identity `SecurityStampValidator` behaviour.

## Goals / Non-Goals

**Goals:**
- Every point is additive for a consumer that changes nothing, except the start-up refusal of
  whole-type encryption on event and aggregate types.
- No schema change in the directory tables.

**Non-Goals:**
- A dedicated "end membership" operation. Writing the membership with status `Former` is that
  operation; *Writing a membership replaces it wholly* already says what happens to its roles.
- Migrating data already written under a tenant into the person's own scope. The associated data binds
  the ciphertext to the tenant; moving it means re-writing it, which is the host's to decide.
- Keeping the tenant across a bearer-token refresh. The framework performs no such refresh itself; a
  host that maps one re-creates the principal through the factory, which resolves from the selection.
  The documentation says so.
- A selection per application. It would let live resolution work across applications, but it changes
  the `active_tenant` table and the store contract, and the host has chosen the kept-and-checked mode.

## Decisions

**`Former` is a status, not a soft-delete flag.** `MembershipStatus.Former = 2`. It inherits every
"only `Active` grants" check as it stands. *Alternative:* a separate `EndedAt` column. Rejected: a
schema change for the same observable behaviour, and every check would have to learn a second field.

**A selection follows its membership out of `Active`.** `SetMembershipAsync` deletes the
`active_tenant` row for `(user, tenant)` whenever the written status is not `Active` — the same
statement `RemoveMembershipAsync` issues — and `InMemoryTenantMembershipStore` does the same. The
claim resolver already ignores such a selection, so this only stops a dead pointer from lingering.
Evidence it is needed: a test that writes `Former` over a selected membership and reads
`GetActiveTenantAsync`.

**Refuse whole-type encryption where it lives in a stream, at start-up and at append.** A new check in
`EncryptionMetadataDriftGuard` (it already walks `ITrustedTypeResolver.RegisteredTypes` and owns the
encryption-metadata invariants) refuses an `IAggregate` with `EncryptionMetadata.ClassAttribute` set,
and every event payload type such an aggregate's `Apply` methods take. The guard reads the `Apply`
parameters itself: the registration's reflection lives in Tier-A `Stratara.Abstractions`, and sharing it
would mean a new public member for eight lines. All
violations are collected and reported in one exception, as `AggregateSnapshotShapeGuard` does.
Because an event type can be registered without an aggregate (`AddTrustedType`, a projection-only
event), `EventSource` refuses at append, before serialising, an event whose payload metadata has a
class attribute — one cached metadata lookup on a path that already does it.
- *Alternative:* make the mapper deliver an event whose whole payload is gone (a `null` `Data`).
  Rejected: every handler would meet a `null` it was never written for, and the event's identity
  would survive with none of its facts — the "silently skips the event" outcome the host ruled out.
- *Alternative:* for snapshots only, fall back to a full replay when a class-encrypted snapshot is
  unreadable. Rejected: it contradicts *An unreadable snapshot fails loudly*, and marking properties
  removes the case altogether.
- *Alternative:* refuse at start-up only. Rejected: it cannot see an event type no aggregate applies.

**The person's own key is named, not only listed.** `SubjectEraser`'s directory scopes for a user gain
`(UserScoped, Guid.Empty, user)` next to `(UserScoped, null, user)`. The serializer is left as it is.
- *Alternative:* normalise `Guid.Empty` to `null` in the serializer's `KeyScope`. Rejected: data
  already written lives under the `Guid.Empty` scope, and new versions would start a second scope for
  the same data; the eraser would have to name both anyway.

**A person's own data is a stream owned by the user alone.** Planned first as a recipe — append under
a session whose tenant is the reserved default — that turned out impossible during implementation: the
event store refuses to take an owner from a session whose tenant is `Guid.Empty`
(`EventSource.ResolveSubjectAsync`), refuses an explicit `EventSubject` with an empty tenant
(`ThrowIfNamesNoTenant`, and the default `IEventSource.CreateOnBehalfOfAsync`), and treats a first entry
with an empty tenant as recording no owner (`LookupStreamOwnerAsync`), so a later append under a tenant's
session would have re-homed the stream. Decided 2026-10-29 under the owner's delegation, recorded here:
- `EventSubject.ForUser(Guid userId)` and `EventSubject.NamesAnOwner` (a tenant, or a non-empty user).
  The explicit-subject check refuses only a subject that names neither; the owner lookup returns a
  user-alone owner from a first entry with an empty tenant and a user; the session path is unchanged,
  so a session that failed closed still writes nothing ownerless.
- Such an entry carries `TenantId = Guid.Empty` and the user; the serializer keys it
  `UserScoped / Guid.Empty / user`, which the eraser now names (below).
- *Alternative:* a host-chosen "person tenant" id. Rejected: the key would name a real tenant id the
  eraser does not know, so a key store that cannot list would leave it behind, and nothing would stop
  that tenant from being erased.
- *Alternative:* a new sensitivity level "person". Rejected: `UserScoped` with no real tenant already is
  that scope; a new level would be a second name for it and a new branch in every coverage rule.
Evidence: `EventSourceOwningUserTests.A_stream_created_for_a_user_alone_keeps_that_owner_under_a_tenants_session`,
`StreamEncryptionAcrossErasureTests.A_persons_own_data_follows_them_across_tenants_and_dies_only_with_them`.

**The kept-and-checked mode is one registration that wires three pieces.**
`AddCheckedMembershipTenantClaim<TUser>()` (name to be settled in review against the two existing
ones):
1. the existing `MembershipClaimsPrincipalFactory<TUser>` decorator, so sign-in and a host's
   re-issue stamp the claim from the selection;
2. a `PostConfigure<SecurityStampValidatorOptions>` whose `OnRefreshingPrincipal` copies the tenant
   claim of `CurrentPrincipal` onto `NewPrincipal`, replacing what the factory stamped, when the
   membership is still `Active`, and removes it when it is not — chained to any handler the host set;
3. an `IClaimsTransformation` that, for an authenticated principal carrying the claim, looks up
   `ITenantMembershipStore.GetMembershipAsync(user, tenant)` and returns a clone without the claim
   when it is not `Active`. A principal without the claim passes through unchanged: this mode never
   resolves live.

Registering it together with `AddMembershipTenantClaimsTransformation()` throws
`InvalidOperationException` naming both — ASP.NET Core resolves a single `IClaimsTransformation`, so
the later registration would silently win.
- *Alternative:* refuse the request (401/403) when the membership has ended. Rejected: the
  framework would have to know the host's authentication schemes and challenge behaviour; removing
  the claim reuses the fail-closed path *Tenant resolution fails closed* already defines, and every
  role- or permission-guarded request is refused by the existing checks.
- *Alternative:* re-resolve to another tenant when the carried one has ended. Rejected: the
  spec already prefers no claim to a guessed one — a wrong tenant fails invisibly.
- *Alternative:* re-check only on refresh (shorter security-stamp interval). Rejected: the host
  requires the loss of access at the next request.

## Risks / Trade-offs

- [A consumer marks an event type as a whole today] → the host stops starting after the upgrade. The
  message names the type and says to mark its properties instead. Data already written stays readable
  as long as its key exists, because the refusal applies to start-up and append, not to reading: the
  serializer recognises a whole-type envelope by its shape whenever the type requires encryption at
  all, so a type whose marking moved to its properties still reads its old payloads.
  CHANGELOG lists it under breaking behaviour.
- [Payloads already written as a whole stay whole] → after their subject's erasure they still stop a
  replay; the refusal prevents only new ones. The documentation says so, and that such a host has to
  decide how to treat those events (an upcaster cannot help, since it sees ciphertext only).
- [One membership lookup per request in the kept-and-checked mode] → the same cost live resolution
  already has; it is a primary-key lookup. Documented next to the mode.
- [A host's own `RefreshSignInAsync` for an unrelated reason re-resolves the tenant from the
  selection] → documented: in this mode a re-issue is the switch. A host that re-issues for other
  reasons first restores the selection or accepts the switch.
- [A key store that cannot list still misses other non-directory scopes] → unchanged and already
  warned about; this change only closes the one scope the framework itself creates for a person.

## Migration Plan

No data migration. `Former` fits the existing column. A host that wants the kept-and-checked mode
replaces `AddMembershipTenantClaim<TUser>()` (and `AddMembershipTenantClaimsTransformation()`, if it
used both) with the new registration. A host that marked an event or aggregate type as a whole moves
the marking to the properties before upgrading; payloads already written keep their whole-type
envelope and are still read.
