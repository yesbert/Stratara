# Stratara.Identity.EntityFrameworkCore

EF Core identity-directory plane for the Stratara stack: user↔tenant membership (many-to-many
with tenant-scoped roles), active-tenant selection, and membership-backed authorization.

## Contents

- `IdentityDirectoryDbContext<TContext>` — standalone DbContext hosting all four directory tables
  (`tenant_membership`, `active_tenant`, `setting_entry`, `api_key`); derive a concrete context and
  own its migrations. To fold these into an existing context instead, call
  `modelBuilder.ApplyIdentityDirectoryModel()` in its `OnModelCreating`.
- `ModelBuilder.ApplyIdentityDirectoryModel()` — adds the same tables to an *existing* context
  (for example your ASP.NET Identity context) so they share one migration lineage.
- `AddTenantMembershipStore<TContext>()` — EF-backed `ITenantMembershipStore`: forward lookup
  (a user's tenants), reverse lookup (a tenant's members), upsert, GDPR sweeps per user/tenant,
  and membership-guarded active-tenant switching.
- `MembershipAuthorizationProvider` / `MembershipAuthorizationProvider<TUser>` — the framework's
  default `IAuthorizationProvider`: role checks pass on tenant-scoped membership roles, the
  `TUser` variant additionally on global ASP.NET Identity roles (platform roles).
- `AddMembershipCrossTenantAuthorizer(...)` — membership-backed `ICrossTenantAuthorizer` for
  strict tenant isolation, with configurable cross-tenant roles for the operator-impersonation
  path (platform administrators without membership in the target tenant).
- `AddPermissionCatalog(...)` + `AddCatalogPermissionResolver[<TUser>]()` — the application's
  code-declared permission vocabulary with role grants, resolved from membership roles (and
  optionally global ASP.NET Identity roles) for `[RequirePermission]` enforcement and HTTP
  permission policies.
- `AddApiKeyStore<TContext>()` — API keys and personal access tokens: `stk_`-prefixed 256-bit
  keys (hash-only storage), fail-closed validation with expiry/revocation, erasure sweeps.
  Machine keys are materialized as tenant memberships, so they flow through the same
  role/permission plane as human actors; PATs act as their bound user. `ImportAsync` stores a
  machine key the caller already holds (generated with `ApiKeyFormat.CreateRawKey()`) for setups
  where the key must exist before boot — idempotent, never mutating an already-stored key.
- `AddSettingCatalog(...)` + `AddSettingStore<TContext>()` — scoped settings plane: code-declared
  `SettingDefinition`s, row-per-key EF storage (`setting_entry`), a Subject-fed `ISettingProvider`
  fallback chain (user-in-tenant → user → tenant → global → configuration → default), transparent
  AES-GCM encryption for `IsEncrypted` definitions, and per-user/per-tenant erasure sweeps.

## Dependencies

- `Microsoft.EntityFrameworkCore` (+ Relational)
- `Microsoft.Extensions.Identity.Core`
- `Stratara.Abstractions`

Used together with `Stratara.Mediator` (authorizing mediator, tenant isolation) and
`Stratara.Identity.AspNetCore` (sign-in claims bridge).
