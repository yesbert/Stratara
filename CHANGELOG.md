# Changelog

All notable changes to the Stratara framework are documented in this file.

The format follows [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/) and the
versioning [Semantic Versioning 2.0.0](https://semver.org/).

Stratara is versioned **lockstep** — all 25 packable packages share the same version
number, controlled by `<VersionPrefix>` in `Directory.Build.props`. A single entry here
applies to the entire NuGet family.

> **Note on entry style.** All entries — both `[Unreleased]` and historical — are written
> in English and focus on consumer-visible changes (API, behaviour, packaging, migration).
> The historical entries were originally written in German with internal-team identifiers;
> they have been rewritten retroactively for public consumption.

## [Unreleased]

_no changes yet since `3.4.0`._

## [3.4.0] — 2026-08-28

Four findings a consumer team reported from production use, three defects found in the framework's
own queue, and one found while fixing another. The thread running through them is the same: a
mechanism that reported success while doing nothing. A signature that covered everything except the
payload. A circuit breaker whose thresholds its own retry could never reach. A replay marking that
outlived the replay. Two environment guards that admitted every environment name nobody had thought
of. Each was documented as working, and each was believed.

**Read the two rollout notes before upgrading a fleet**: bus-envelope signatures change, and a
projection replay marking already stuck from an earlier version does not clear itself on upgrade.

### Added

- **`ProjectionReplayOptions`** — configures how long a projection replay's active marking and
  progress counters survive without renewal. Registered with its defaults by
  `AddProjectionReplayState()`, so an existing host is leased without configuring anything.

- **`AddTenantMembershipStoreFromContextFactory<TContext>()`,
  `AddApiKeyStoreFromContextFactory<TContext>()` and `AddSettingStoreFromContextFactory<TContext>()`**
  — register the identity-directory stores so that each operation takes a fresh database context from
  `IDbContextFactory<TContext>` instead of sharing the request's.

  The existing registrations share one context across every directory store in a request. A database
  context serves one operation at a time, so directory work issued concurrently within a scope — two
  role checks together, a lookup racing a page load — fails on whichever arrives second, and the
  failure surfaces at the call site that lost the race rather than the one that introduced the
  concurrency. Sharing also means a store's own commit commits whatever else the consumer has left
  unsaved on that context.

  Both registrations now state what they cost, because the choice runs both ways: with a context per
  operation neither of those applies, and in exchange a store write no longer takes part in a
  transaction opened on the consumer's own scoped context. Calling both registrations for the same
  store leaves whichever ran first in place; they do not compose.

  Nothing changes for a consumer who does not adopt them — the existing registrations behave exactly
  as before, and remain the default.

### Fixed

- **Appending on behalf of a subject that names no tenant now fails.** Of the five sources the store
  consults to decide which tenant an event belongs to, four rejected an absent tenant and the append
  failed rather than guessing. The fifth — the subject you supply yourself through
  `AppendOnBehalfOfAsync` — was taken as given, so passing one with an empty tenant id recorded an
  event owned by nobody and encrypted against nobody. An erasure never reaches such an event. The
  call now throws `ArgumentException` naming the stream and the event, and records nothing.
  **Breaking** where a consumer passes a subject sourced from aggregate state that a stream written
  before the field existed left empty — that call site is the defect, and the exception message
  identifies it.

- **A newly created tenant now owns itself.** The framework's tenant-creation event did not declare
  the tenant it creates as the event's data owner, so ownership fell through to the acting session:
  a tenant created by an operator was owned by the *operator's* tenant, while a caller who happened
  to point the session at the new id first got a tenant that owned itself. The same operation
  produced two different owners depending on whether the caller knew to take an undiscoverable
  detour. **Breaking** in recorded data: tenants created after adopting this version are owned by
  themselves. Streams already written are untouched, no payload changed, and no migration is
  required — the event's serialized JSON is byte-identical.

- **An abandoned projection replay no longer suppresses publication forever.** While a replay is
  active the framework suppresses publication, so replayed history does not re-trigger side effects.
  That marking was only ever cleared by the process that set it, so a kill, a container stop, an
  out-of-memory kill or a reboot left it standing with nothing to clear it — and while it stands,
  every command is recorded instead of sent while the caller receives an identifier and a success
  response, and the outbox never drains. The marking and its progress counters are now held on a
  lease that the replay renews each time it reports progress: a replay that stops without clearing
  them stops renewing them, and they lapse on their own.

  The lease defaults to 300 seconds and is configurable through the new `ProjectionReplayOptions`.
  Set it longer than your slowest stretch between two progress reports — a shorter lease lapses while
  the replay is still running and resumes publication against half-rebuilt read models. Erring long
  only delays the clearing of a marking whose replay already died.

  **One action on upgrade:** a marking already stuck from before this version was written without an
  expiry and does not gain one. Clear it once — an explicit deactivation, or let the next replay's
  own completion clear it. Upgrading alone does not free a host that is stuck today.

- **The outbox drain no longer spins on a batch it cannot deliver.** The worker read a batch, handed
  it to the dispatcher, and read again until a batch came back empty — but an entry is only removed
  once the bus has accepted it, so a batch nobody could deliver came back identical every time. Two
  ordinary conditions reached it: an unreachable broker, and a projection replay in progress. Either
  turned a drain pass into a hot loop against the database, and because the command drain never
  returned, the event drain that follows it never started. A drain pass now handles one batch and
  ends; undelivered entries stay stored and are retried on the next interval.

  Two shifts to know about. **`outbox.published` now counts what the bus accepted** rather than what
  was read from storage — it is emitted by the dispatchers, which know the answer, instead of by the
  worker, which did not. The instrument name and its `outbox.kind` tag are unchanged, but the values
  drop to the truth, so an alert threshold calibrated against the inflated numbers needs revisiting.
  And **a large stored backlog now drains at one batch per polling interval** rather than in a single
  pass — with the defaults, twenty thousand entries a minute, and both the batch size and the
  interval are configurable.



- **Two environment guards now admit Development only, instead of refusing Production only.**
  `IsProduction()` recognises exactly one name. Everything else fell through — Staging, QA, UAT,
  Preview, and, because the check is by name, `Production-EU` and `prod` as well.

  **Missing broker credentials** no longer fall back to the `guest` account outside Development.
  Previously any non-Production host silently connected as `guest`, which matters where the broker
  runs in the same container or network with a default configuration.

  **The no-op email sender** can no longer be registered outside Development. It returns success for
  every operation without an exception or a log entry, so on a staging host a registered user never
  received a confirmation link, the account stayed unconfirmed, and external-login auto-linking then
  failed for that user. What gets reported is "OIDC linking doesn't work on staging"; the cause is a
  dropped mail three steps earlier.

  **Breaking for hosts that relied on either fall-through**, which is the point of the change. Both
  escape hatches already exist and are explicit — set `RABBITMQ_USERNAME=guest` and
  `RABBITMQ_PASSWORD=guest` to use the default account deliberately, and register your own
  `IEmailSender<TUser>` to drop mail outside Development. The framework ships no no-op sender for
  that case, because a shipped no-op is indistinguishable at every call site from a working one.

- **The message-bus circuit breaker can now open.** It could not — not rarely, but never. The breaker
  required ten failures inside a sixty-second window while the retry in front of it backs off to a
  sixty-second cap, so once the backoff had grown at most one failure landed per window and the tenth
  was never counted. Measured over a simulated hour of uninterrupted failure: sixty-three attempts,
  no circuit opening at all. The window is now derived from the retry's own delay cap instead of being
  chosen independently of it, and five failures inside a ten-minute window open the circuit.

  **Nothing you can observe was broken, and nothing you rely on changes.** Retries remain unbounded,
  the backoff is unchanged, and traffic still succeeds once the broker recovers without you writing
  any retry — the retry sits in front of the breaker and retries the broken-circuit signal too. What
  changes is what a sustained outage *looks like*: an open circuit in logs and metrics, which is a
  state you can alert on. If your broker goes down for minutes, expect new breaker-state events.

  Worth saying plainly: the framework documented this protection and did not have it. An operator who
  built an alert on breaker state got an alert that could never fire, and the code comment describing
  the behaviour described something that never happened.

### Security

- **A bus-envelope signature now covers the payload, not only the identity claims.** The canonical
  form a signature was computed over was, for an event bundle, everything except the events — the
  record has three fields and one of them is the signature. That is correct for the threat it was
  built for, which is *minting* a session context. It does not answer *transplanting* an observed
  signature onto different events, which was never raised.

  The attack is not "replay the same message" — duplicates are ordinary, delivery is at-least-once
  and handlers are already idempotent. It is: observe one signed message, publish a new one carrying
  that exact session context and arbitrary events, and the canonical form is unchanged, so the
  signature verifies and strict mode accepts it. The attacker cannot reach a tenant they have never
  observed, which is a real limit. They can inject arbitrary events into any tenant they *have*
  observed one message from — the projection worker writes them into that tenant's read models, and
  the saga worker reacts to them and can issue commands as that actor. Two of the three actors this
  mechanism was designed to stop plausibly hold read access as well as publish access.

  The canonical form now covers a digest of the payload — the events for a bundle; the command body
  and the envelope id for a command, both of which were transmitted and unsigned. It is built from
  field values, never by re-serializing, so a message that has been on the wire projects to exactly
  what its publisher signed.

  **Fields are now length-prefixed.** They were joined with a separator that two attacker-controlled
  fields are allowed to contain, so content could be shifted across a field boundary without
  changing the canonical form — altering which command type is dispatched while the signature still
  verified, defeating the guard the type name is signed for.

  **Rollout — this is the migration the three modes exist for, and the first time it is needed.**
  Signatures produced by an older publisher stop verifying. Move the fleet through **permissive**
  mode: publishers upgrade first, consumers follow, then strict is safe again. Upgrading both sides
  straight into strict will reject in-flight messages.

- **The integrity start-up warning now fires outside development, not only in production.** It was
  governed by whether the host was *named* `Production`, so a host called `Production-EU` or `prod`
  ran with unsigned envelopes and no warning. It is now governed by whether the host is in
  development, which also surfaces the deviation on staging and QA.

## [3.3.0] — 2026-08-25

Two capabilities the framework advertised but did not provide, and a set of guards for behaviour
that failed by succeeding. Crypto-shredding was the headline feature with no way to perform an
erasure; a web host had one mapper for the four failure types it has to answer for. Alongside those,
three things that reported success while doing nothing now report the truth instead — which is why
this release carries breaking entries despite adding no signature you have to change.

### Added

- **`ISubjectEraser` — an erasure is now one call.** Crypto-shredding was the framework's headline
  capability with no way to perform an erasure: four separate sweeps existed, nothing composed them,
  and a consumer had to know all four and get the order right themselves. `AddStrataraErasure()`
  registers a composed operation that sweeps API keys, scoped settings, directory memberships and
  key material — in that order, because a credential that still works can act mid-erasure and
  shredding the key first makes every other plane unreadable. It reports what each plane covered,
  and stops at the first failing plane rather than continuing, so a failed settings sweep is never
  followed by the key shred. What it deliberately does **not** cover is documented: read models your
  own projections built, unprotected event-stream data, the command audit log and outbox, and
  system-wide key material.
- **`AddStrataraProblemDetails()` — one error shape for every framework rejection.** The framework
  raises three failure types a web host has to answer for and shipped a mapper for one of them, so
  both consumers wrote the missing half themselves. The new handler maps a validation rejection to
  `400` with the failures grouped by the field each concerns, and an authorization refusal or
  tenant-access denial to `403`, all as RFC 7807 problem responses. It is opt-in and converts nothing
  else: any failure the framework did not raise propagates untouched, so a host keeping its own error
  model simply does not register it.
- **`ITransaction.SaveChangesIdempotentAsync` — the idempotent-projection write the framework was
  writing by hand.** A projection sees the same event twice under at-least-once delivery, and a row
  can vanish between the read and the write; neither is a fault. The helper commits, and on a
  concurrency conflict asks whether the write's target still exists: gone means a concurrent bundle
  reached the same end state and the commit is satisfied, still there means a real conflict and the
  exception is rethrown so the bundle fails as it must. `TenantProjection` now uses it — and gains
  the distinction, having previously swallowed every conflict.
- **Three guides that did not exist**: evolving an event schema, using the resilience policies, and
  writing an update handler. Each leads with the constraint that costs most to discover late — an
  upcaster sees ciphertext and snapshots are never upcasted; a retry re-runs your handler; a property
  missing on either side of an update is ignored in silence.

### Changed

- **BREAKING — the development key store no longer pretends to erase.** `DummyKeyStore.RevokeAsync`
  and `EraseScopeAsync` completed successfully and shredded nothing, so a consumer exercising an
  erasure path in development got a green result and no shredding. Both now throw
  `NotSupportedException`. The store derives a single key from a fixed pass-phrase and holds no key
  material it could destroy; reporting success was the defect. Register a real `IKeyStore` — the
  file-backed envelope store, an HSM, Key Vault, KMS — to exercise crypto-shredding.
- **BREAKING — a host refuses to start when an aggregate cannot be restored from its snapshot.** An
  aggregate property that holds state and cannot be set from outside the type is not restored by a
  snapshot: the aggregate rebuilds, the events after the snapshot apply, and only what the snapshot
  held for that property is gone — silently, and worse the better snapshotting works.
  `AddEventSourcing()` now scans the registered aggregates at start-up and fails, naming the
  aggregate and the property. Give it a public setter; aggregates use `public set` rather than
  `private set` precisely because snapshot deserialization needs it. Computed properties are
  unaffected — they hold no state and are recomputed after a restore.
- **BREAKING — test-support packages are now kept out of projects that are not test projects.** Both
  `Stratara.Testing` and `Stratara.Testing.EntityFrameworkCore` ship an MSBuild check that fails the
  build with `STRATARA1001` when a referencing project is neither a test project nor opted out. Set
  `StrataraAllowTestSupportOutsideTests=true` for a deliberate exception such as a sample or a
  benchmark. The check travels with the package, so it fires on a `PackageReference` and not on a
  project reference inside a single solution.
- **`AddStrataraTestingEventStore` refuses to register into a running host.** It wires an in-memory
  SQLite database and in-memory doubles, which would start successfully in production and lose every
  write. It now throws `InvalidOperationException` when a registered `IHostEnvironment`, or
  `DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT`, names anything other than `Development`. Where no
  environment is stated at all — the ordinary unit test, which has no host — the call is allowed, so
  existing test suites are unaffected.
- **`command.duration` is documented as what it measures.** It was described as end-to-end command
  latency and is recorded only in the outbox worker, so a host dispatching in-process saw an empty
  histogram and a host doing both saw half its traffic. The instrument and its tags are unchanged —
  only the description.
- **Settings resolve in one read per scope instead of one per setting and scope.** Twenty inherited
  settings over a four-scope fallback chain were up to eighty store round trips; each scope is now
  loaded once. Same fallback order and same results.
- **Snapshot evaluation no longer opens an extra transaction per stream** on the write hot path.

### Deprecated

- **`UseAuthorizationExceptionTo403()`, removed in the next major version.** It maps two refusals to
  a bare status code with no body; `AddStrataraProblemDetails()` supersedes it with a full problem
  response that also covers validation. It keeps working for now — the compiler warning is the
  migration notice. Do not register both: the middleware answers first and the handler never sees the
  exception.

## [3.2.3] — 2026-08-22

Three correctness fixes, all of the same kind: behaviour that failed by succeeding. A pipeline
behaviour registered twice ran twice and nothing said so; a closed generic type name normalized to a
malformed key that two different types could share, and the loser of that collision was discarded
silently; and a heavy command republished from the outbox left its lane whenever its type could not
be resolved. None produced an error, and no test covered any of them. They were found by reading the
code against the specification rather than by a failure report.

### Fixed

- **A pipeline behaviour registered twice now installs once.** Every behaviour registrar used a
  plain scoped registration, so a host that called one twice — the ordinary result of composing two
  service bundles that each set up their own slice of the framework — installed the stage twice and
  ran it twice per request. None of the three consequences was benign: every validator ran twice
  against every request, so a validator with a side effect (a uniqueness check against the database)
  performed it twice; the tenant guard ran twice, the second time against a second options instance
  that could disagree with the first; and two nested resilience pipelines multiplied rather than
  added, turning a configured budget of four attempts into sixteen. `AddStrataraValidation`,
  `AddStrataraTenantIsolation`, `AddStrataraResilienceBehavior`, `AddCommandAuditing` and the
  `AddPipelineBehavior` / `AddPipelineBehaviorWithResult` primitives are now idempotent per behaviour
  type. Registering two *different* behaviours is unaffected.
- **A closed generic type name now normalizes correctly.** Resolving a recorded type name is meant to
  ignore the assembly version and match on the type name and assembly name alone. For a closed
  generic it did neither: the name was truncated at its second comma, which for a generic falls
  *inside* the type-argument brackets, so the outer assembly name was dropped and the key became a
  malformed fragment. Two closed generics that differed only in the assembly that declared them
  collapsed onto one key, and the second registration was then silently discarded — an event
  upcaster could match the wrong source type, and a type a host believed it had registered was
  unresolvable. Names are now parsed rather than counted, and each type argument is reduced the same
  way as the outer name, so upgrading the *payload's* assembly no longer strands rows either.
- **A heavy command republished from the outbox stays in the heavy lane.** When a stored command's
  recorded type could not be resolved in the process draining the outbox, republication fell back to
  the shared command topic regardless of the lane the command belonged to. Depending on how the lanes
  are deployed, the command was then either dead-lettered by the interactive worker or executed on
  the interactive lane — the starvation the separate lane exists to prevent. The lane is now recorded
  on the envelope when the command is enqueued, so republication no longer depends on resolving the
  type.

### Changed

- **Registering two different types under one recorded name now fails.** The trusted-type resolver
  previously kept the first and discarded the second without a word, which is indistinguishable from
  a registration that never happened: the type simply fails to resolve later, when a stored row is
  read. `ITrustedTypeResolver.Register` now throws, naming both types. Registering the same type
  again remains a no-op. Reaching this requires two distinct types sharing a full type name *and* a
  simple assembly name in one process.
- **`TenantIsolationOptions` follows the options pattern.** The mode can now be bound from
  configuration with `Configure<TenantIsolationOptions>(section)` in addition to the
  `AddStrataraTenantIsolation(o => ...)` callback, and a second call to the registrar no longer
  leaves a conflicting second options instance behind. `Stratara.Mediator` therefore takes a
  dependency on `Microsoft.Extensions.Options`.

### Added

- **`CommandEnvelope.Heavy`** records whether a command declared itself long-running when it was
  enqueued. Optional and defaulting to `false`, which is how an envelope written by an earlier
  version deserializes; the signed canonical form is unchanged, so existing signatures still verify.

## [3.2.2] — 2026-08-14

API keys can now be bootstrapped. Issuance always generated the key itself, which does not fit the
setups where server and caller have to share a key *before* either starts — container orchestration,
CI provisioning, self-hosted bundles, end-to-end test hosts. This release adds an import path for a
key the caller already holds, plus the canonical key format as public API so callers generate values
instead of inventing them.

### Added

- **`IApiKeyStore.ImportAsync` — store a machine key whose raw value the caller already holds.**
  Issuance generates the key itself, which is the wrong shape when server and caller must share it
  *before* either boots: container orchestration, CI provisioning, self-hosted bundles, end-to-end
  test hosts. Without this path, consumers bypassed the store and reimplemented its internals (the
  digest format and the machine-key membership row) — a copy that keeps compiling after the store
  changes and fails as a runtime 401. Import is idempotent, so it can run unconditionally on every
  boot: a value that is already stored returns the existing descriptor and the stored key is never
  mutated, so a changed configuration cannot escalate a key's roles or extend its expiry unnoticed.
  Revoked, expired, and foreign-tenant values are rejected rather than silently adopted, and
  concurrent replicas racing the same first import converge on one key. Machine keys only —
  `ApiKeyImportRequest` carries no `UserId`.
- **`ApiKeyFormat` (`Stratara.Abstractions.ApiKeys`) — the canonical raw-key format as public API.**
  `CreateRawKey()` generates `stk_` plus the Base64Url encoding of 32 CSPRNG bytes; `IsWellFormed()`
  checks that shape. `ImportAsync` accepts only well-formed values: the store keeps its stored
  digest unsalted because a generated key carries 256 bits of entropy, and a hand-picked value would
  quietly invalidate that. Generate keys out of band with `CreateRawKey()` and keep them in a secret
  store — the type lives in the abstractions package so host-builder and orchestration projects can
  reach it without referencing the storage implementation.
- **`InMemoryApiKeyStore` (`Stratara.Testing`) — the drop-in double for the API-key store**,
  mirroring issuance, import, fail-closed validation, revocation, and the erasure sweeps, and
  materializing machine keys into a membership store you can share and inspect.

### Changed

- **API-key lifecycle events are now logged** (`Stratara.Diagnostics` event-ID range 116_000):
  import, idempotent repeat, and a repeat that supplied different parameters. Key ids and tenant
  ids only — never the raw key or its digest.
- **For implementers of `IApiKeyStore`:** the interface gained `ImportAsync`. Custom implementations
  need the new member; callers are unaffected.

## [3.2.1] — 2026-07-28

### Fixed

- **Snapshot loads are now scoped to the aggregate type.** The snapshot read previously filtered
  only by stream id, so when two aggregate types shared a stream id, rehydrating one type could
  pick up the other type's (higher-versioned) snapshot and deserialize it into the requested type —
  silently returning an aggregate with default/empty fields. Snapshot reads now match the aggregate
  type (version-independently, so an assembly rev-bump does not invalidate existing snapshots), and
  snapshot writes are grouped per `(stream, aggregate type)` so a stream carrying more than one type
  snapshots each type independently. A snapshot of type X may now only ever rehydrate type X.

### Changed

- **`ISnapshotRepository` gained type-scoped read overloads.** `GetAsync(streamId,
  aggregateTypeName, toVersion, ct)` and `GetLatestVersionOrDefaultAsync(streamId,
  aggregateTypeName, ct)` are the correct lookups; the previous type-less overloads are now
  `[Obsolete]` because they can return a foreign aggregate type's snapshot on a shared stream.
  Custom `ISnapshotRepository` implementers should add the new overloads.

## [3.2.0] — 2026-07-17

### Added

- **User↔tenant membership (`Stratara.Identity.EntityFrameworkCore`, new package).** First wave of
  the identity-directory plane: a many-to-many membership model (`TenantMembership` +
  `ITenantMembershipStore` in `Stratara.Abstractions.Multitenancy`) records which tenants a user
  belongs to and which tenant-scoped roles the user holds in each — single-tenant apps are the
  degenerate case of one membership. The EF-backed store ships both lookup directions (a user's
  tenants, a tenant's members), upsert semantics, per-user/per-tenant erasure sweeps for GDPR
  flows, and a membership-guarded active-tenant selection for users who belong to more than one
  tenant. Host the tables in your existing DbContext via
  `modelBuilder.ApplyIdentityDirectoryModel()` (one migration lineage) or derive a standalone
  context from `IdentityDirectoryDbContext<TContext>`; register with
  `AddTenantMembershipStore<TContext>()`. The lockstep family grows from 24 to 25 packable
  packages.
- **Membership-backed authorization providers.** The framework now ships its first
  `IAuthorizationProvider` implementations: `MembershipAuthorizationProvider` (role checks pass on
  tenant-scoped membership roles within the session's data-owner tenant) and
  `MembershipAuthorizationProvider<TUser>` (additionally consults global ASP.NET Identity roles —
  platform roles such as a platform administrator — on a membership miss). Wire directly
  (`AddMembershipAuthorization[<TUser>]()`) or through the authorizing mediator
  (`AddAuthorizingMediator<MembershipAuthorizationProvider<TUser>>()`). Both are fail-closed.
- **Membership-backed cross-tenant authorizer.** `AddMembershipCrossTenantAuthorizer(...)`
  replaces strict tenant isolation's deny-all default with stored facts: a cross-tenant operation
  passes when the actor holds an active membership in the data-owner tenant or one of the
  configured `CrossTenantRoles` (the operator-impersonation path for platform administrators).
- **Sign-in tenant-claim bridge (`Stratara.Identity.AspNetCore`).** The `stratara:tenant_id`
  claim the session-context middleware reads is now emitted by the framework instead of
  consumer-owned code. Two composable modes: `AddMembershipTenantClaim<TUser>()` decorates the
  ASP.NET Identity claims factory and stamps the claim into every issued principal (cookies and
  Identity bearer tokens), and `AddMembershipTenantClaimsTransformation()` resolves it live per
  request so tenant switches apply without re-issuing the sign-in. Tenant resolution prefers the
  user's persisted active-tenant selection, falls back to the only/first active membership, and
  stamps nothing for users without an active membership (tenant resolution stays fail-closed).
  Principals that already carry the claim pass through untouched.
- **`InMemoryTenantMembershipStore` (`Stratara.Testing`).** Drop-in membership-store double
  mirroring the EF store's contract semantics, including the membership-guarded active-tenant
  selection and the erasure sweeps.
- **Permission-based authorization (`[RequirePermission]`).** Fine-grained sibling of
  `[RequireRole]`: applications declare their permission vocabulary once in a code-first
  `PermissionCatalog` (`AddPermissionCatalog(c => { c.Add("sims.read"); c.GrantToRole("TenantAdmin",
  "sims.read"); })` — granting an undeclared permission throws, so grant typos surface at startup),
  and guard commands/queries with `[RequirePermission("sims.read")]`. Enforcement runs in the
  authorizing mediator and the authorizing outbox dispatcher (multiple attributes AND; missing
  permission throws the new `PermissionAuthorizationException`, which derives from
  `AuthorizationException` so existing 403 mappings catch it unchanged; `AuthorizationException` is
  now unsealed for that reason). The mediator's startup validator fails fast when
  permission-guarded types exist without an authorizing mediator or without a registered
  `IPermissionResolver` — a permission attribute can never be silently ignored. Role guards are
  unaffected; roles and permissions compose freely on the same request type.
- **Catalog permission resolvers (`Stratara.Identity.EntityFrameworkCore`).**
  `AddCatalogPermissionResolver()` maps the actor's active tenant-scoped membership roles through
  the catalog's role grants; `AddCatalogPermissionResolver<TUser>()` additionally maps global
  ASP.NET Identity roles (platform roles), so `GrantToRole` works regardless of which level a role
  lives on. Resolution is memoized per `(userId, tenantId)` within a scope and fail-closed;
  permission sets are deliberately never carried in `SessionContext`, cookies, or tokens.
- **HTTP permission policies (`Stratara.Identity.AspNetCore`).** `AddStrataraPermissionPolicies()`
  turns every declared catalog permission into an on-demand ASP.NET Core authorization policy —
  plain `[Authorize("sims.read")]` / `.RequireAuthorization("sims.read")` works without hand-registering
  policies. Undeclared policy names defer to the default provider; evaluation goes through the
  registered `IPermissionResolver` (user id from the name-identifier claim, tenant scope from
  `stratara:tenant_id`), never through claims-embedded permissions.
- **Scoped settings plane (`Stratara.Abstractions.Settings` + `Stratara.Identity.EntityFrameworkCore`).**
  Provider-neutral settings with four scopes — global, tenant, user, user-in-tenant. Applications
  declare their vocabulary code-first in a `SettingCatalog` (`AddSettingCatalog(c => c.Add(new
  SettingDefinition("Ui.Theme", "system")))`); values are stored row-per-key via `ISettingStore`
  (EF default: `setting_entry` table, registered with `AddSettingStore<TContext>()`, exact-scope
  reads/writes, `null` deletes). The `ISettingProvider` read facade resolves for the current
  session's Subject through the fixed fallback chain user-in-tenant → user → tenant → global →
  host configuration (`Stratara:Settings:<name>`) → code default, with typed access
  (`GetAsync<T>`); definitions with `IsInherited = false` consult only the most specific scope.
  Reading or redeclaring an undeclared name throws — typos surface immediately.
- **Encrypted settings + GDPR erasure.** Definitions marked `IsEncrypted = true` are stored
  AES-GCM-encrypted at rest through the security plane's `ISecureBlobEncryptor` — key scope
  derived from the setting scope and the purpose bound to the setting name, so a leaked row can
  neither be decrypted under another scope's key nor replayed as a different setting; because the
  keys live in `IKeyStore`, `EraseScopeAsync` crypto-shreds a user's encrypted settings.
  `ISettingStore.DeleteScopeAsync` completes the plane's erasure story: a user scope sweeps the
  user's values across all tenants, a tenant scope sweeps the tenant's values across all users.
- **`InMemorySettingStore` (`Stratara.Testing`).** Drop-in settings-store double mirroring the EF
  store's contract semantics, including the widened erasure-sweep behavior.

- **API-key / personal-access-token authentication (`IApiKeyStore` + `StrataraApiKey` scheme).**
  Machine-to-machine auth as a standard ASP.NET Core authentication scheme, not a parallel
  system. `AddApiKeyStore<TContext>()` (Stratara.Identity.EntityFrameworkCore) issues
  `stk_`-prefixed keys with 256-bit entropy — the raw key is shown exactly once, storage holds
  only its SHA-256 digest (`api_key` table, unique hash index) — and validates fail-closed
  (unknown/revoked/expired → null). **Machine keys are materialized as tenant memberships of the
  key id**, so role checks, permission resolution, and cross-tenant authorization treat a key
  exactly like a human actor; revocation and the tenant-erasure sweep remove those memberships
  again. **Personal access tokens** bind a key to a user (issuance requires the user's active
  membership; PATs carry no roles of their own) and authenticate as that user. On the HTTP side,
  `AddStrataraApiKey()` (Stratara.Identity.AspNetCore) registers the handler — `X-Api-Key`
  header by default, opt-in `access_token` query for header-less transports — whose ticket
  carries the name-identifier and `stratara:tenant_id` claims the session-context middleware
  already reads.
- **Auth-scheme selector (`AddStrataraAuthSchemeSelector()`).** A policy scheme that routes each
  request by shape — API-key header → API-key scheme, `Authorization: Bearer` → bearer scheme,
  everything else → the cookie scheme (all three routable scheme names configurable) — so mixed
  API/browser hosts set one default scheme instead of per-endpoint scheme lists.
- **External-login OpenID Connect + JWT-bearer helpers (`Stratara.Identity.AspNetCore`).** Two
  configuration-driven authentication-builder extensions add the external identity providers as
  ordinary schemes. `AddStrataraOpenIdConnect(configuration)` wires the interactive "log in with
  &lt;provider&gt;" flow (Entra, Keycloak, generic OIDC) from an `Identity:OpenIdConnect` section;
  `AddStrataraJwtBearer(configuration)` validates API access tokens from an `Identity:JwtBearer`
  section, routing a multi-issuer API by the token's `iss`. Both key the principal on the issuer
  `sub`, never on email. The package now depends on
  `Microsoft.AspNetCore.Authentication.OpenIdConnect` and
  `Microsoft.AspNetCore.Authentication.JwtBearer`.
- **Hardened JIT external-login provisioning (`AddStrataraExternalLoginProvisioning<TUser>()`).** On
  a user's first external sign-in, `ExternalLoginProvisioningService<TUser>` creates a local
  ASP.NET Identity account and links the external login, or links to an existing account — with the
  account-takeover defenses as hard defaults, not opt-ins. It links on the issuer's `(provider,
  sub)` (never on the mutable email claim); auto-links to a pre-existing account only when the email
  is verified by the provider (`email_verified`/Entra `xms_edov`) **and** already confirmed locally,
  returning `RequiresInteractiveLinking` rather than merging otherwise; honors an optional
  invitation gate and an `AutoProvision` switch; and fails closed on every unsatisfied check. The
  callable service leaves the sign-in callback/UI to the consumer; `Stratara.Sample.Identity`
  demonstrates the full wiring.
- **Documentation and runnable samples for the whole capability.** Five new guides cover the
  identity surface — tenant membership and the sign-in claim bridge, permission-based authorization,
  scoped settings, API keys and personal access tokens, and external login with JIT provisioning.
  Two samples back them: the new `Stratara.Sample.IdentityDirectory` runs membership, permissions
  and scoped settings end to end against SQLite, and `Stratara.Sample.Identity` gains the API-key
  lane alongside its external-login wiring. Both are smoke-tested in CI like every other sample.

### Changed

- **`AddNpsqlWriteDbContextFactory` corrected to `AddNpgsqlWriteDbContextFactory`** (`Stratara.EventSourcing.EntityFrameworkCore`).
  The write-side context-factory extension was missing the `g` in "Npgsql". The correctly-spelled
  name is now canonical; the old spelling remains as an `[Obsolete]` alias that forwards verbatim,
  so hosts registered against the 3.1.x name keep compiling. The alias will be removed in the next
  major version.
- **`AddAzureServiceBus` now replaces the `IMessageBus` registration** (`Stratara.Outbox.AzureServiceBus`).
  Previously it used `TryAdd`, so calling it after the RabbitMQ umbrella (`AddMessaging()`, which the
  worker composites call) was a silent no-op that left RabbitMQ in place. It now `Replace`s the slot,
  so an explicitly-chosen transport wins regardless of registration order. Use one transport per host.

### Fixed

- **Authorization decorators now guard the runtime request type.** The authorizing mediator's
  void-request path and the authorizing outbox dispatcher previously read `[RequireRole]` (and now
  `[RequirePermission]`) attributes from the statically inferred generic type. A command dispatched
  through a base-typed variable (class-hierarchy dispatch) therefore skipped guards declared on the
  derived type. Both decorators now inspect `request.GetType()`, so runtime-type guards are always
  enforced — strictly fail-closed relative to the previous behavior.
- **Public docs and package pages corrected against source.** A pre-release audit found the READMEs,
  DocFX guides, `llms.txt`, and several csproj `<Description>` fields describing APIs and behavior the
  code does not have — fabricated extension methods, a compile-breaking `KeyScope` snippet, quick
  starts that threw at runtime, `internal` types shown as wire-up, and an overstated
  "automatic verification" claim for tamper-evident streams. All corrected; a new
  `scripts/check-doc-symbols.py` gate fails the build on any documented `Add*`/`Map*`/`Use*` call
  that no longer resolves in `src/`.

## [3.1.7] — 2026-07-01

### Added

- **Event upcasting (`IEventUpcaster`).** Persisted events can now be transformed from an older
  on-disk schema into the shape the current event record expects, applied on read before the payload
  is deserialized. Register upcasters with `AddEventUpcaster<TUpcaster>()`; each implements
  `IEventUpcaster` (`SourceEventTypeName` / `TargetEventTypeName` plus `JsonNode Upcast(JsonNode)`) and
  they chain across successive schema versions (v1→v2→v3), including event-type renames — the source
  type no longer needs to exist in the assembly. Matching ignores the assembly `Version` segment, and
  a cyclic or duplicate-source chain is rejected. Hosts that register no upcasters are unaffected
  (transparent pass-through). Snapshots are not upcasted — change an aggregate's shape and rebuild its
  snapshots.

### Changed

- **Copyright holder corrected to the full legal name "Norbert Heinz Rosenwinkel"** in the `LICENSE`
  file and in the `Authors` / `Copyright` package metadata. No code or licensing-terms change — the
  license remains MIT.

## [3.1.6] — 2026-06-22

### Changed

- **License changed from FSL-1.1-MIT to the MIT License.** Stratara is now OSI-approved open
  source — free for any use, including commercial, with no competition clause and no two-year
  conversion delay. The previous Functional Source License (source-available, converting to MIT
  two years after each release) has been replaced outright. Package metadata now declares the
  SPDX expression `MIT` (`PackageLicenseExpression`), so nuget.org renders a standard clickable
  MIT license label instead of an embedded custom-license file. The `LICENSE` file at the repo
  root now contains the standard MIT text and is still bundled into every package. No code or API
  changes accompany this — it is purely a licensing and metadata change. Previously published
  versions (3.0.20 through 3.1.5) remain under the FSL terms they shipped with; this change applies
  to all versions released from here on.

## [3.1.5] — 2026-06-22

### Added

- **Configurable snapshot strategy** (`Stratara.Abstractions`, `Stratara.Infrastructure`) — the
  snapshot cadence is no longer hard-coded. A new `Stratara.Abstractions.EventSourcing.ISnapshotStrategy`
  decides, per stream, whether the event-sourcing runtime should write a snapshot:
  `bool ShouldSnapshot(Type aggregateType, long currentVersion, long lastSnapshotVersion)`.
  `AddEventSourcing()` registers the default `VersionThresholdSnapshotStrategy` (snapshot every 50
  versions — identical to the previous behaviour) via `TryAddSingleton`, so existing consumers see no
  change. To take over the policy, register your own singleton `ISnapshotStrategy` (it overrides the
  default whether registered before or after `AddEventSourcing()`): vary the threshold per aggregate
  type, construct `new VersionThresholdSnapshotStrategy(threshold)` for a different uniform cadence, or
  register `NoSnapshotStrategy` to disable snapshotting entirely. This replaces the previously
  hard-coded `UseSnapshots`/`SnapshotRange` constants in the default snapshot service, which were not
  actually configurable despite the documentation implying they were.
- **`AddDomainEventTypesFromAssemblyContaining<T>()`** (`Stratara.Abstractions`) — registers *only* the
  domain event types consumed by an assembly's aggregate `Apply(TEvent)` methods in the trusted-type
  resolver, without registering the aggregate types themselves and without pulling any projection / saga
  / command-handler classes into DI. Use it in a host that only needs to deserialize event payloads off
  the message bus or event stream — typically a dedicated projection or saga worker — but must not wire
  the handler classes (and their runtime dependencies) that `AddProjectionsFromAssemblyContaining<T>`
  would register. Complements the existing `AddAggregatesFromAssemblyContaining<T>` (which additionally
  registers the aggregate types) and `AddTrustedType<T>` (single type).

## [3.1.4] — 2026-06-15

### Added

- **Command-workload isolation (heavy-command lane)** — long-running commands can now be routed to a
  dedicated worker lane so they cannot starve interactive commands. Mark a command with the new
  `Stratara.Abstractions.Mediator.IHeavyCommand` marker and the `ICommandOutboxDispatcher`
  automatically publishes it to a separate heavy-command topic (`IMessagingIdentifier.HeavyCommandTopic` /
  `HeavyCommandSubscription`, configurable under `Messaging:HeavyCommand`, defaulting to `heavy-command` /
  `heavy-command-subscription`). Run a dedicated heavy-command worker with the new
  `services.AddHeavyCommandWorker(degreeOfParallelism?)` extension, or the
  `builder.AddHeavyCommandWorkerServices(degreeOfParallelism?)` host composite — in the same process as
  the interactive worker (two lanes) or in a separately scaled host. Each worker's degree of parallelism
  is configurable per lane. `IMessagingIdentifier` gains `HeavyCommandTopic`, `HeavyCommandSubscription`,
  and the `GetCommandTopic(Type)` / `GetCommandSubscription(Type)` routing helpers. The interactive lane
  (`AddMediatorWorker()`) is unchanged and remains the default; commands not marked heavy keep their
  existing routing. If a heavy command is dispatched while no heavy worker is bound, the publish is
  rejected and the command is preserved in the outbox until a heavy-command worker comes online — it is
  never dropped. Works over both the RabbitMQ and Azure Service Bus message buses (Azure Service Bus
  requires the heavy-command topic/subscription to be provisioned, like the existing command topic). New
  log-event ID `105_005` (`CommandWorkerLaneStarted`) in `Stratara.Diagnostics`.
- **Observability metrics across the worker pipeline** (`Stratara.Diagnostics`) — the shared
  `Stratara.Service` meter now publishes throughput and latency instruments so operators can see how the
  event-sourcing pipeline is behaving instead of flying blind on a single counter. New instruments:
  `event_source.events.appended` (counter, tagged by `event.type` / `aggregate.type`),
  `outbox.published` (counter, tagged by `outbox.kind` = `command` / `event`), `command.duration`
  (histogram, ms, tagged by `request.type` / `outcome`), `projection.events.processed` (counter) +
  `projection.bundle.duration` (histogram, ms), `saga.events.processed` (counter) +
  `saga.bundle.duration` (histogram, ms), and `saga.inflight` (up/down counter). They are recorded by the
  event source, command worker, projection worker, saga worker, and outbox worker respectively. Because
  projections and sagas are real-time bus subscribers without a persisted checkpoint, these report
  **throughput and latency**, not consumer lag. No configuration is required — point any OpenTelemetry
  metrics exporter at the `Stratara.Service` meter.
- **Operational health checks for the event store and outbox** (`Stratara.EventSourcing.EntityFrameworkCore`) —
  two opt-in readiness checks added to any `IHealthChecksBuilder`: `AddEventStoreHealthCheck()` verifies
  the write-side database is reachable, and `AddOutboxHealthCheck(degradedThreshold?, unhealthyThreshold?)`
  reports the pending outbox backlog (exposed under the `pending` data key) and escalates to
  `Degraded` / `Unhealthy` when the backlog crosses the supplied thresholds. Both are tagged `ready` by
  default (so they map to a readiness endpoint, not liveness) and require the Stratara write store to be
  registered. The write-store DbContext is now also resolvable as a scoped `IWriteDbContext` service to
  support these checks.
- **Polly-backed mediator resilience behavior** (`Stratara.Resilience`) — an opt-in pipeline behavior
  wraps the in-process dispatch of a request marked with the new
  `Stratara.Abstractions.Resilience.IResilientRequest` in the named Polly pipeline the request selects
  (`ResiliencePipelineName`). Register it with the new `AddStrataraResilienceBehavior()` (after
  `AddStrataraValidation()` / `AddStrataraTenantIsolation()` so the retry wraps the handler, not the
  guards); requests without the marker are unaffected. A new built-in pipeline
  `ResilienceNames.ConcurrencyConflict` retries **only** on
  `Stratara.Abstractions.Persistence.ConcurrencyConflictException` (5 attempts, short exponential
  backoff) so a handler that re-reads and re-applies on an optimistic-concurrency clash succeeds without
  bespoke retry loops; it is registered by `AddResiliencePipelines()` alongside the existing message-bus
  and dispatcher pipelines. Only mark handlers that are safe to re-run (idempotent or concurrency-guarded).

## [3.1.3] — 2026-06-10

### Added

- **Mediator tenant-isolation behavior** (`Stratara.Mediator`) — `AddStrataraTenantIsolation()`
  registers a pipeline behavior that enforces tenant isolation at the mediator entrance, before the
  handler runs, for any request that opts in via the new `ITenantScopedRequest` marker
  (`Stratara.Abstractions.Multitenancy`). The behavior compares the request's `TenantId` (data owner)
  against the ambient session's data-owner tenant and rejects a mismatch with the new
  `TenantAccessDeniedException` (translated to HTTP 403 on ASP.NET hosts). `TenantIsolationMode.Default`
  enforces only the subject match (privileged cross-tenant operations pass when the endpoint promoted
  the session subject to the target); `TenantIsolationMode.Strict` additionally routes every
  cross-tenant operation through the new `ICrossTenantAuthorizer`, whose shipped default denies all
  cross-tenant access until a consumer registers its own authorizer. Complements the existing
  database-side `ApplyGlobalTenantQueryFilters` with a command-/query-entrance guard. New log-event
  IDs `114_101`/`114_102`/`114_003` in `Stratara.Diagnostics`.
- **`Stratara.Abstractions.Persistence.ConcurrencyConflictException`** — provider-agnostic
  wrapper for an optimistic-concurrency conflict detected during commit. Allows framework-level
  code in `Stratara.Projections` (and any consumer outside the `EntityFrameworkCore` package) to
  react to concurrency without taking an EF Core dependency. EF Core's `DbUpdateConcurrencyException`
  (and provider equivalents) flow through this type.

### Changed

- **`EfTransaction.SaveChangesAsync`** (in `Stratara.EventSourcing.EntityFrameworkCore`) now
  wraps `DbUpdateConcurrencyException` thrown by EF Core in the new
  `ConcurrencyConflictException`. PostgreSQL unique-violation paths remain on `DbUpdateException`
  (different semantics — duplicate-key on insert vs. stale-row on update/delete).
- **`EventSource.SaveChangesAsync`** (write-side append flow) extends its concurrency-handling
  catch to the new exception type so the existing append-conflict recovery path keeps working
  after the wrap. Behaviour for both EF concurrency conflicts and PostgreSQL unique violations
  is unchanged.

### Fixed

- **`TenantProjection` no longer aborts the event bundle on a parallel delete race.** The two
  delete handlers (`TenantDeleted`, `CustomerTenantsDeleted`) now swallow
  `ConcurrencyConflictException` silently — a missing row is the desired end-state of a delete.
  Before this fix, a consumer-side customer-delete cascade saga that emits both
  `CustomerTenantsDeleted` and a follow-up `TenantDeleted` for the same tenants would race the
  two parallel projection bundles on the same `TenantView` row; the loser threw
  `DbUpdateConcurrencyException` out of `SaveChangesAsync`, which propagated through
  `ProjectionWorker` and caused `RabbitMqBus` to roll back the entire bundle — including
  sibling projections that had already committed. Update handlers (rename / activate /
  deactivate / locale / customer-assigned) keep their current behaviour: a concurrency failure
  there is a real race that propagates.

## [3.1.2] — 2026-06-05

### Added

- **New package `Stratara.Testing`** — test doubles and assertion helpers so consumers can
  unit-test Stratara-based code without Postgres or RabbitMQ testcontainers. Reference it from
  test projects only.
  - `AggregateTestHarness<T>` + `Aggregate.Rehydrate<T>(...)` — given/when/then rehydration of an
    aggregate from events using the same reflection-based `Apply(...)` dispatch as the production
    aggregation service. It throws on an event with no matching `Apply` overload so a forgotten or
    mistyped overload fails the test; opt back into the production-lenient skip with
    `IgnoringUnmappedEvents()`.
  - `InMemoryKeyStore` — an `IKeyStore` that mints random 256-bit DEKs per `KeyScope` and supports
    rotation / revocation / scope-erasure without a master KEK or key file.
  - `TestBlobEncryptor.CreateAesGcm()` — the real AES-GCM `ISecureBlobEncryptor` over an
    `InMemoryKeyStore`, so blob round-trips exercise production encryption.
  - `InMemoryMessageBus` — an `IMessageBus` with synchronous in-process dispatch and a `Published`
    list for assertions.
  - `TestSessionContext` / `TestSessionContextProvider` — preset Actor/Subject `SessionContext`
    values and an `ISessionContextProvider` double.
  - `TestTenants.Of("acme")` — stable, deterministic tenant/user ids from readable slugs.
    `TestSessionContext` sets both correlation and causation ids so the context can drive
    event-store writes.
  - `TestEvent.Create(payload, ...)` — wrap an event payload in `IEvent<T>` with realistic
    metadata; `ProjectionTester.HandleAsync(projection, event)` — invoke a projection's private
    `HandleAsync` handler directly to unit-test it against mocked repositories.
- **New package `Stratara.Testing.EntityFrameworkCore`** — spins up the **real** event-sourcing
  write stack (`IEventSource`, `IAggregationService`, snapshots, the EF Core write store) against a
  shared in-memory SQLite database in one call, so tests exercise production code paths without
  Postgres or Docker. Reference it from test projects only.
  - `EventStoreTestHost.Create(...)` — owns the SQLite connection + service provider; exposes
    `ExecuteAsync(IEventSource)`, `AggregateAsync<T>(streamId)`, the preset `Session`, and the
    recording `Outbox`.
  - `AddStrataraTestingEventStore<TWriteDbContext>(connection, tenantId)` — the lower-level DI
    extension; `StrataraTestWriteDbContext` — a ready-made concrete write context;
    `RecordingEventBundleOutboxDispatcher` — captures emitted bundles for assertions.
  - The lockstep family grows from 22 to 24 packable packages.

## [3.1.1] — 2026-06-01

### Fixed

- **`FileMasterKeyProvider` now rejects a master KEK that is not exactly 32 bytes at startup.**
  The KEK is used directly as an AES-256-GCM key, which accepts only 16/24/32-byte keys. The
  provider previously required merely *at least* 32 bytes, so a longer KEK (for example the
  48-byte output of `openssl rand -base64 48`, a common HKDF master-key recipe) passed both
  construction and the eager `FileKeyStoreStartupProbe`, then threw
  `CryptographicException: Specified key is not a valid size for this algorithm` on the **first**
  key creation at runtime — defeating the purpose of the boot-time probe. The provider now
  validates the decoded length is exactly 32 bytes and fails fast at boot with an actionable
  message (`Generate one with: openssl rand -base64 32`). A 32-byte KEK is unaffected.
- **`EnvelopeFileKeyStore` is now safe for multiple processes sharing one store file** (for
  example several containers bind-mounting the same host directory). Previously a process only
  read the store once at construction, so a data-encryption key created by another process after
  startup was invisible (`GetDataEncryptionKeyAsync` returned `null`, breaking decryption), and
  two processes creating keys concurrently could overwrite each other's keys or mint colliding
  versions for the same scope. Reads now reload from disk on a cache miss (guarded by the file's
  last-write time to avoid reload storms), and every mutation serializes through an exclusive
  cross-process lock file and re-reads the latest on-disk state before writing. A networked file
  system (NFS/SMB) remains unsupported — it guarantees neither atomic rename nor reliable advisory
  locks.

### Added

- **`LogEvents.KeyManagement.KeyStoreReloaded` (112_006)** — debug-level event emitted when the
  file key store reloads its state from disk to pick up keys written by another process.

## [3.1.0] — 2026-05-30

**Breaking release.** The `IKeyStore` and `ISecureBlobEncryptor` contracts changed shape, a new
dependency-light `Stratara.Security` package now owns the production key store and envelope
encryption, and a new vendor-neutral `Stratara.Validation` package adds request validation as a
`Stratara.Mediator` pipeline behavior. Consumers must recompile and adapt call sites; data
encrypted under the previous HKDF-style key model is **not** binary-compatible and needs a
re-encrypt pass on its own schedule.

### Added

- **`Stratara.Validation` — vendor-neutral request validation.** A new package providing a
  mediator pipeline behavior that runs `IValidator<T>` implementations before the handler and
  throws an aggregated `StrataraValidationException` on failure. Register with
  `AddStrataraValidation()` (outermost behavior) and `AddValidatorsFromAssemblyContaining<T>()`.
  Only `ValidationSeverity.Error` blocks the request; `Warning`/`Info` failures pass through and
  are logged. The package has no FluentValidation dependency — the contract is intentionally
  FluentValidation-shape-compatible so an optional adapter can be added later.
- **Validation contracts in `Stratara.Abstractions`** (namespace `Stratara.Abstractions.Validation`):
  `IValidator<T>`, `ValidationResult`, `ValidationFailure`, `ValidationSeverity`, and
  `StrataraValidationException`. Declaring the exception in `Stratara.Abstractions` lets a
  consumer's global exception handler map validation failures to its own error model (e.g.
  RFC-7807 ProblemDetails) without referencing the behavior package.
- **`Stratara.Security` — production key store + envelope encryption (dependency-light).** Adds
  `EnvelopeFileKeyStore`, a file-backed `IKeyStore` storing **KEK-wrapped, versioned per-scope
  data-encryption keys** (rotation, single-version revoke, and whole-scope crypto-shred), plus a
  `FileMasterKeyProvider` (`IMasterKeyProvider`, the KEK custody seam), an AES-GCM
  `ISecureBlobEncryptor`, and the Development-only `DummyKeyStore`. Register with
  `AddStrataraFileKeyStore(configuration)`. The package references only `Stratara.Abstractions` +
  BCL crypto + `Microsoft.Extensions.*` abstractions — no EF Core, RabbitMQ, Redis, or cloud SDKs —
  so lean consumers can encrypt without pulling in `Stratara.Infrastructure`.
- **New security contracts in `Stratara.Abstractions.Security`:** `KeyScope`, `KeyMaterial`, and
  `IMasterKeyProvider`.

### Changed

- **BREAKING — `IKeyStore`.** Replaced `EnsureKeyAsync(level, Guid? tenantId, Guid? userId)` with
  `GetOrCreateCurrentKeyAsync(KeyScope)` returning `KeyMaterial` (key id + bytes in one call), and
  added `RotateAsync(KeyScope)` and `EraseScopeAsync(KeyScope)`. `RevokeAsync(string keyId)` now
  performs a real crypto-shred (the production store no longer treats it as a no-op). Scope
  identifiers are `string?` (carrying both slugs and `Guid.ToString()` values) rather than `Guid?`.
- **BREAKING — `ISecureBlobEncryptor`.** `EncryptAsync`/`DecryptAsync` now take a `KeyScope` and a
  `purpose` instead of a bare `Guid tenantId`. The encrypted stream gains a leading version byte
  (v2) and a `purpose` field; legacy streams without the version byte remain readable (configurable
  via `Stratara.Security` options).
- The AES-GCM encryption factory, blob encryptor, and dev key store moved out of
  `Stratara.Infrastructure` into `Stratara.Security`; `AddSecurity()` now delegates to it. The
  field/JSON `[EncryptData]` path (`ISecureJsonSerializer`) stays in `Stratara.Infrastructure`.

This brings the lockstep family to **22 packable packages**.

## [3.0.23] — 2026-05-28

**First publication to nuget.org.** The complete 20-package Stratara family is now
available on the public [nuget.org](https://www.nuget.org/packages?q=Stratara) feed in
addition to the internal Azure Artifacts feed. External consumers can now run
`dotnet add package Stratara.Mediator` (and the rest of the family) directly. This release
carries the same API and behaviour as 3.0.22 — the only change is public distribution.

### Changed

- **Distribution.** Packages are now pushed to nuget.org on every `v*` tag, gated behind a
  manual approval, in addition to the existing internal feed. Symbol packages (`.snupkg`)
  are forwarded to symbols.nuget.org automatically.

## [3.0.22] — 2026-05-28

**Public-surface polish for the GitHub mirror.** Branded README header with logo and
badges, a *Why we share this* Vision block making the FSL-1.1-MIT-to-MIT two-year
commitment explicit, and richer Use-cases sections in all three concept docs tying the
integrity properties to concrete compliance frameworks (GDPR, SOC 2, HIPAA, ISO 27001,
PCI-DSS). No API or behaviour change.

### Added

- **Logo + branded README header.** The repository logo (`docs/assets/logo.png`) is
  embedded centred at the top of the README, with a tagline, license badge, docs badge,
  and a .NET 10 badge underneath. Matches modern GitHub README hero-section conventions.
- **"Why we share this" section in the README.** A short Vision block explaining why
  Stratara is published — to make integrated CQRS, Event Sourcing, audit-grade integrity,
  and tenant-aware encryption available in the .NET ecosystem as defaults instead of as
  enterprise-tier add-ons — and the FSL-1.1-MIT-to-MIT two-year conversion commitment.
- **Use-cases sections in all three concept docs**:
  - `docs/concepts/tenant-aware-encryption.md` — GDPR Article 17 crypto-shredding,
    multi-tenant SaaS offboarding, backup leak defense, SOC 2 / ISO 27001 / HIPAA /
    PCI-DSS compliance posture, insider-threat reduction.
  - `docs/concepts/tamper-evident-streams.md` — financial-transaction integrity, HIPAA
    Security Rule §164.312(c), forensic admissibility, insider-threat detection, SOC 2
    Type 2 operating-effectiveness evidence.
  - `docs/concepts/why-event-sourcing.md` — GDPR Article 30 (records of processing),
    Article 15 (subject access), SOC 2 audit trail, forensic readiness.

### Changed

- **README *Why Stratara* Encryption block** now mentions crypto-shredding and GDPR
  Article 17 explicitly — the most concrete value prop of tenant-bound AAD encryption,
  previously buried in the concept doc.

## [3.0.21] — 2026-05-28

**Documentation and samples refresh.** Two hero samples make Stratara's two
integrity-by-construction properties — hash-chained event streams and tenant-bound field
encryption — visible in ~100 lines of zero-dependency code each. Three new concept docs
explain the *why* behind those properties. The README and DocFX landing page are
rewritten with a verb-first tagline and a three-block *Why Stratara* section that
surfaces the differentiators above the fold. No API or behaviour change.

### Added

- **Two hero samples** that demonstrate Stratara's two integrity-by-construction
  properties in ~100 lines of zero-dependency in-memory code:
  - `Stratara.Sample.TamperProof` — appends three events into a SHA-256 hash chain,
    simulates a direct-DB mutation, and shows the verifier catching the break at the exact
    sequence number. Mirrors what the `EventStreamHashing` worker does in production.
  - `Stratara.Sample.Encryption` — seals the same plaintext under two tenants with
    AES-256-GCM, demonstrates same-tenant decryption succeeds, cross-tenant decryption
    fails with `AuthenticationTagMismatchException` because the AAD (tenant id) does not
    match — *even with the correct master key*. Mirrors the `[EncryptData]` value-converter
    behaviour in production.
- **Three concept docs** under `docs/concepts/` covering the *why* behind the framework:
  Tamper-Evident Streams, Tenant-Aware Encryption, and Why Event Sourcing. Each links to
  the corresponding hero sample and the relevant operator guide.

### Changed

- **README and documentation landing page rewritten** with a verb-first tagline ("CQRS and
  Event Sourcing for .NET — with tamper-evident streams and tenant-aware encryption built
  in.") and a three-block *Why Stratara* section that surfaces the integrity properties
  above the fold. Samples section split into a *Hero Samples* block (the why-Stratara
  demos) and a *Learning Path* block (the CQRS → Event Sourcing → Saga progression).
- **Smoke-test coverage** extended to assert both hero samples' `stdout` line-for-line, so
  any future API drift in the integrity story breaks CI rather than rotting silently.

### Process (accumulated since `3.0.20`)

- GitHub mirror auto-sync via the `SyncToGitHub` pipeline stage (triggers on `v*` tags).
- Versioning policy formalised as a three-lane model (no-bump / preview / stable tag).
- Community files for the GitHub mirror (`CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`,
  `SUPPORT.md`, issue / PR templates, auto-close workflow for external pull requests).

## [3.0.20] — 2026-05-28

**Public GitHub identity and SourceLink.** Package metadata now points at the public
GitHub mirror, and SourceLink emits source URLs into `.pdb` files and `.snupkg` symbol
packages — debugger step-into into Stratara source is available from any consuming
application once the GitHub mirror is reachable.

### Changed — NuGet metadata points at GitHub

- `PackageProjectUrl` is now `https://github.com/yesbert/Stratara`.
- `RepositoryUrl` is now `https://github.com/yesbert/Stratara.git`. Consumers on
  `nuget.org` plus tools like `dotnet nuget locals` and IDE "show in source repository"
  actions now resolve to the public GitHub page.

### Added — SourceLink for GitHub

- `Microsoft.SourceLink.GitHub` 8.0.0 is added as a global `PackageReference` in
  `Directory.Build.props` with `PrivateAssets="all"` (CPM entry in
  `Directory.Packages.props` under a new `SourceLink` group). Embedded source URLs land
  in the `.pdb` files and `.snupkg` symbol packages, so debugger step-into into
  Stratara source works out of the box for consuming applications.

### Notes for consumers

No API or behaviour change. Pure metadata refresh. Consumers on 3.0.20+ see the GitHub
URL in their NuGet package details. URLs in older package versions remain in place —
the internal Azure Artifacts feed continues to be the primary distribution channel.

## [3.0.19] — 2026-05-27

**Public-mirror cleanliness.** Editorial defence against internal-documentation leaks
on the public surface. A few public-facing XML doc comments referenced internal
convention files; those references have been removed (with the substantive content
inlined where needed), and a CI guard now prevents the regression.

### Changed

- **Public-facing XML docs** (`IHasRowVersion`, `WriteDbContext<T>`, `ReadDbContext<T>`,
  `WorkerDefaultsHostBuilderExtensions`) no longer reference internal convention
  documents. Where the reference carried information (for example *DbContext
  configuration isolation*), that information is now inline in the XML doc. The change
  affects the auto-generated DocFX API reference and the `<doc>.xml` files shipped in
  each `.nupkg`; no behaviour change.
- **Historical CHANGELOG entries** rewritten so they no longer mention internal
  documents — public readers see "internal docs updated …" or a directly rewritten
  description instead.
- **`docs/overview/what-is-stratara.md`** architecture-rules note generalised to the
  convention "no consumer-specific code".

### Added

- **New CI script** `scripts/check-public-mirror.sh` — scans `src/**/*.cs`,
  `docs/**/*.{md,yml,json}`, `samples/**`, `CHANGELOG.md`, and `README.md` for
  internal-doc paths and fails with diagnostic output on any hit. Escape hatch via a
  `stratara-allow-claude-ref` token for intentional mentions.
- **`local-gauntlet.sh`** invokes the cleanliness check as the last step (after DocFX,
  before pack).

### Consumer migration

None. Pure editorial and documentation hygiene. The `.xml` files inside each `.nupkg`
change, but **no API behaviour and no type signatures change**.

## [3.0.18] — 2026-05-27

**Public docs site.** A dedicated DocFX documentation site now lives under `docs/`.

### Added

- **DocFX site under `docs/`** — five sections: `overview/` (what-is-stratara,
  architecture-at-a-glance with tier diagram, glossary), `getting-started/`
  (prerequisites, first-stratara-app, di-composition), `guides/` (nine how-tos covering
  command handlers, projections, sagas, `[EncryptData]`, auth decorators, the RabbitMQ
  and Azure Service Bus outboxes, the HMAC bus envelope, and testing patterns),
  `reference/` (DI-extensions cheatsheet, routing conventions, log-events schema, plus
  an auto-`mref` API reference generated from XML docs of all 20 packable tier-A/B/C
  packages), and `samples/` (one walkthrough per sample).
- **Stratara icon** wired in as the app logo (header) and favicon. `icon.png` is copied
  from the repo root into `_site/assets/` by the DocFX resource block.
- **`scripts/local-gauntlet.sh`** optionally invokes `docfx build docs/docfx.json
  --warningsAsErrors` (only when the `docfx` global tool is installed; otherwise skipped
  with an install hint).
- **`.gitignore`** entries for `docs/_site/`, `docs/reference/api/`, and `docs/obj/`
  (generated outputs, not committed).

### Consumer migration

None. Purely additive documentation site, **no public-API surface change**.

## [3.0.17] — 2026-05-27

**Sample smoke coverage.** The five samples are no longer build-only in CI — they are
now launched as real subprocesses, their stdout is captured, and key phrases are
asserted. Plus locale pinning so the demos look identical on every machine.

### Added

- **New `Stratara.Samples.SmokeTests` project** under `tests/Stratara.Samples.SmokeTests/`
  — xUnit v3 / MTP test suite that launches each of the five samples (`CqrsBasics`,
  `EventSourced`, `OutboxWorker`, `MoneyTransferSaga`, `AspNetCoreApi`) as a subprocess
  via `Process.Start("dotnet", "sample.dll")`, captures `stdout`/`stderr` synchronously,
  and asserts on concrete output phrases — catches API breaks where the samples compile
  but blow up at runtime.
- **`SampleRunner` helper** with two modes: `RunUntilExit` for self-terminating samples
  (synchronous stream draining, deterministic across platforms) and `RunUntilMarker` for
  `AspNetCoreApi` (waits for `"Now listening on:"`, performs an HTTP POST/GET round-trip,
  kills the process tree). Optional environment-variable overrides (e.g. `ASPNETCORE_URLS`
  with a free-port pick).
- **`SampleResultAsserts` helpers** (`ContainsInStdOut`, `ExitCodeIs`, `StdOutEndsWith`)
  that dump the full sample context (exit code, stderr, full stdout) on failure instead
  of the truncated xUnit default diagnostic.

### Changed — deterministic sample output

- **All five samples** set `CultureInfo.DefaultThreadCurrentCulture =
  CultureInfo.GetCultureInfo("en-US")` at the start of `Program.cs`. Previously the
  `{balance:C}` currency-format output depended on the host's default locale (macOS =
  `$175.00`, hosted Ubuntu CI = `¤175.00` with the generic currency symbol). The demos
  now look identical everywhere.

### CI

- **`azure-pipelines-unit-tests.yml`**: the "Build Samples" step is replaced by a
  "Sample Smoke Tests" step (build + run via a dedicated bash script). The sample
  build now runs as a side effect of the `ProjectReference`s in the SmokeTests csproj.
- **Local gauntlet** picks up `Stratara.Samples.SmokeTests` automatically through the
  existing `tests/*/*.csproj` pattern — no script change needed.

### Consumer migration

None. Test/CI addition plus sample-locale polish, **no public-API surface change**.

## [3.0.16] — 2026-05-27

**Logging-policy consistency for change-set and event-relevance logs.** A SonarQube
nightly flagged a CA1873 information-level finding in
`LoggerChangeSetExtensions.LogChangeSetCreated`: the convenience overload accepting
`IReadOnlyList<ChangeDetail>` ran `string.Join(...)` unconditionally. While fixing it
we also revisited two pre-existing `IsEnabled(LogLevel.Debug)` guards in
`ProjectionManager.RunProjectionIfRelevant` and `SagaManager.RunSagaIfRelevant` that
violated the source-generated-logging-only policy. All three sites are now solved
uniformly with deferred-formatting wrapper structs whose `ToString()` runs only when
the source-gen formatter has determined the log channel is enabled.

### Added

- **Public tier-B type `DistinctEventTypeNames` (Stratara.Shared)** — wraps
  `IReadOnlyList<IEvent>`; `ToString()` renders a distinct comma-separated list of
  `EventTypeName`. Consumed as a deferred-formatting parameter by
  `LogEventsNotRelevantForProjection` and `LogEventsNotRelevantForSaga`.
- **Public tier-C type `ChangeSetFieldNames` (Stratara.Projections)** — wraps
  `IReadOnlyList<ChangeDetail>`; `ToString()` renders only the changed property names
  (PII-safe). Consumed as a deferred-formatting parameter by `LogChangeSetCreated`.

### Changed (BREAKING) — source-gen logger signatures

- **`LoggerChangeSetExtensions.LogChangeSetCreated`** — source-gen parameter
  `string fieldNames` → `ChangeSetFieldNames fieldNames`. The
  `IReadOnlyList<ChangeDetail>` convenience overload remains source-compatible.
- **`LoggerProjectionExtensions.LogEventsNotRelevantForProjection`** — source-gen
  parameter `string eventTypeNames` → `DistinctEventTypeNames eventTypeNames`.
- **`LoggerSagaExtensions.LogEventsNotRelevantForSaga`** — same: `string eventTypeNames`
  → `DistinctEventTypeNames eventTypeNames`.
- **`ProjectionManager.RunProjectionIfRelevant`** — `IsEnabled(LogLevel.Debug)` guard
  removed; calls `logger.LogEventsNotRelevantForProjection(events.Count,
  new DistinctEventTypeNames(events), projectionName)` directly.
- **`SagaManager.RunSagaIfRelevant`** — same: guard removed; calls
  `logger.LogEventsNotRelevantForSaga(events.Count, new DistinctEventTypeNames(events),
  sagaName)` directly.

### Fixed

- Argument evaluation in the `LogChangeSetCreated` convenience overload now runs
  deferred via `ChangeSetFieldNames.ToString()` inside the source-gen formatter
  (closes CA1873).

### Test coverage

- New regression anchor `LogChangeSetCreated_DefersFieldNameJoinWhenDebugDisabled` in
  `LoggerChangeSetExtensionsTests` — verifies via `EnumerationThrowingChangeList` that
  the change list is not enumerated when debug logging is disabled.

### Consumer migration

The three affected source-gen methods are framework-internal diagnostics; external
consumers typically don't call them directly. Any direct callers need to construct
`new DistinctEventTypeNames(events)` or `new ChangeSetFieldNames(changeSet)` instead of
passing a pre-joined `string` — the wrapper construction is allocation-free.

## [3.0.15] — 2026-05-26

**Final polish before nuget.org publication.** A deep audit found two HIGH findings,
three MEDIUM findings, and three LOW findings. All non-LOW findings plus the
non-debatable LOWs are addressed here.

### Changed — file ↔ class-name rename mismatches

- **`src/Stratara.Infrastructure/DependencyInjection/BackgroundTasksExtensions.cs` →
  `BackgroundTaskExtensions.cs`** (file is singular, matching the class
  `BackgroundTaskExtensions`). The `AddBackgroundTasks` method (plural) is unchanged.
- **`src/Stratara.Infrastructure/DependencyInjection/AuthorizationServiceCollectionExtensions.cs`
  → `AuthorizingCommandOutboxDispatcherServiceCollectionExtensions.cs`** (file now
  matches the class `AuthorizingCommandOutboxDispatcherServiceCollectionExtensions`).
  Also eliminates a filename collision with the file of the same name in
  `src/Stratara.Mediator/DependencyInjection/`. Pure filesystem rename, no API change.

### Changed (BREAKING) — `AspCoreIdentity…` namespace and class rename

- **`AspCoreIdentityServiceCollectionExtensions` →
  `AspCoreIdentityHostBuilderExtensions`** and **namespace
  `Stratara.Identity.AspNetCore.DependencyInjection` → `Microsoft.Extensions.Hosting`.**
  The class exposes `extension(IHostApplicationBuilder builder)` members
  (`AddAspNetIdentity`, `AddAspNetIdentityWithSignInManager`,
  `AddDevelopmentNoOpEmailSender`); per Microsoft convention,
  `IHostApplicationBuilder` extensions belong in `Microsoft.Extensions.Hosting`, and
  the class suffix should be `HostBuilderExtensions` (consistent with
  `WorkerDefaultsHostBuilderExtensions` since 2.0). Consumers with
  `using Stratara.Identity.AspNetCore.DependencyInjection;` must change it to
  `using Microsoft.Extensions.Hosting;` — most hosts already have that import via the
  `builder.Environment`-pattern.

### Documentation

- **`BusEnvelopeIntegrityOptions` XML doc** now spells out the signature scope (threat
  model). A new `<remarks>` paragraph states explicitly: HMAC covers the
  `BusEnvelopeCanonical` projection (identity-only) — for `CommandEnvelope` this is
  `CommandTypeName + "|" + SessionContextJson`; for `EventBundle` it is
  `SessionContextJson`. The payload body (`CommandJson`, `Events[]`) is **not** signed.
  Tamper resistance on payload fields comes from `[EncryptData]` AAD binding (AES-GCM);
  unencrypted fields are not authenticated. Adopters who need payload tamper protection
  must either annotate fields with `[EncryptData]` or add application-layer validation.

### Test coverage

- **Five new tests** in
  `Stratara.Outbox.RabbitMQ.Tests/Messaging/RabbitMqBusProductionGuardTests.cs`:
  Production + missing username/password → `InvalidOperationException`; Production +
  only username or only password missing → throws; Development and Staging → no
  `InvalidOperationException` (fallback remains). Regression anchor for the v3.0.14
  production fail-fast guard.

### Changed — doc polish

- **Ghost directory `src/Stratara.SessionContext/`** (with stale `bin/obj` from
  2026-05-20, leftover from the Sessions rename) has been deleted completely. It is no
  longer referenced by `Stratara.slnx`, the publish solution filter, or SonarQube.

### Changed — cosmetic

- **`src/Stratara.Resilience/Resilience/ResilienceFactory.cs`** trailing newline added.

## [3.0.14] — 2026-05-26

**Closeout bundle before nuget.org publication.** A multi-agent audit (security,
public API, code quality, packaging + architecture) swept the repository once more
before the first public push. Three HIGH findings, two MEDIUM findings, and a clutch
of LOW/INFO findings. All MEDIUMs and LOW/INFOs except those with breaking-change
risk are resolved here; two of the three HIGHs (`PackageProjectUrl` / `RepositoryUrl`
flip to GitHub and `Microsoft.SourceLink.GitHub` wire-up) are scheduled for a later
release as explicit pre-publish gates.

### Removed — `Stratara.SourceGenerators` package dropped (Blazor leak)

- **The `Stratara.SourceGenerators` package is gone.** It bundled exclusively
  `SafeExcludeFromInteractiveRoutingAttribute` (marker) and
  `SafeExcludeFromInteractiveRoutingAttributeGenerator` (Roslyn incremental
  generator) — both emitted Blazor-specific `[ExcludeFromInteractiveRouting]` code
  under `#if BLAZOR_WEB`. A Blazor concern doesn't belong in an application-agnostic
  framework. The `src/Stratara.Generators/` and `src/Stratara.Attributes/` csproj
  files are deleted; the Stratara NuGet family shrinks from 21 to 20 packages.
  **Migration:** consumers that need the attribute and generator should copy them
  1:1 into their own repository; Stratara retains no traces.
- Secondary: `Stratara.SourceGenerators` removed from the tier layout, the
  `README.md` package map, SonarQube coverage exclusions, the publish solution
  filter, and `Stratara.slnx`. The `tests/Stratara.Generators.Tests/` project is
  deleted.

### Changed (BREAKING) — `Tenant.DefaultLocale` and `TenantView.DefaultLocale` default to `string.Empty`

- **`Stratara.Domain.Multitenancy.Tenant.DefaultLocale`** and
  **`Stratara.Projections.Multitenancy.Models.TenantView.DefaultLocale`** both default
  to `string.Empty`. Previously they were hard-coded to `"de-DE"` — a German locale on
  a public aggregate signals "built for a German consumer" and violates the
  application-agnostic rule. Consumers that created `Tenant` streams without an
  explicit `DefaultLocale` in the `TenantCreated` event will see `string.Empty`
  instead of `"de-DE"` on snapshot rehydration. In practice this is trivial because
  the apply-method pattern always takes the value from the event — the default is
  only observed in the `new Tenant()` initial value before any apply.

### Changed (BREAKING) — `IServiceCollection` extensions move to the `Microsoft.Extensions.DependencyInjection` namespace

- **23 DI extension classes change their container namespace.** Previously 29
  extensions lived in `Microsoft.Extensions.Hosting`, but Microsoft's convention is:
  `IServiceCollection` extensions in `Microsoft.Extensions.DependencyInjection`,
  `IApplicationBuilder` extensions in `Microsoft.AspNetCore.Builder`, and
  `IHostApplicationBuilder` / `IHostBuilder` / `WebApplication` extensions in
  `Microsoft.Extensions.Hosting`. We now follow the convention:
  - **22 `*ServiceCollectionExtensions` files plus `BackgroundTasksExtensions`** →
    `Microsoft.Extensions.DependencyInjection`
  - **`AuthorizationExceptionApplicationBuilderExtensions`** →
    `Microsoft.AspNetCore.Builder`
  - **Six `ServiceDefaults` and `WorkerDefaults` extensions** stay in
    `Microsoft.Extensions.Hosting` (legitimately — they hang off
    `IHostApplicationBuilder` or `WebApplication`, per Aspire convention).
- Consumers whose only discovery hook for these extensions was
  `using Microsoft.Extensions.Hosting;` now also need
  `using Microsoft.Extensions.DependencyInjection;` — almost every host program
  already has that import via the `builder.Services.Xxx()` pattern, so in 90% of
  cases the migration is a no-op.

### Security

- **`BusEnvelopeIntegrityStartupProbe` now optionally injects `IBusEnvelopeSigner?`**
  and logs `LogWarning` (EventId
  `LogEvents.BusEnvelopeIntegrity.IntegrityEnabledWithoutSigner` = `113_002`) at host
  start whenever `Mode != Off && signer is null`. Without this, the verifier silently
  accepts every unsigned envelope under `Permissive`.
- **New log event** in `LogEvents.BusEnvelopeIntegrity` (113_002).
- **`RabbitMqBus` refuses to fall back to `guest`/`guest` in production.** It now
  additionally injects `IHostEnvironment` and throws `InvalidOperationException` from
  `CreateConnectionFactory()` when `RABBITMQ_HOST` is set but `RABBITMQ_USERNAME` or
  `RABBITMQ_PASSWORD` is missing AND `IHostEnvironment.IsProduction() == true`.
  Previously the bus silently fell back to the default `guest/guest` account
  (default-restricted to localhost in modern RabbitMQs, but a risk on misconfigured
  brokers). Dev and Staging keep the fallback — same pattern as `DummyKeyStore` since
  v3.0.11.

### Changed — code-quality polish

- **`ResilienceFactory`** extracts `DefaultDispatcherRetryAttempts = 3` and
  `DefaultDispatcherRetryDelay = TimeSpan.FromMilliseconds(200)` as constants and
  consolidates the identical bodies of `CreateCommandDispatcherPipeline` and
  `CreateEventBundleDispatcherPipeline` into an `AddDispatcherRetry` helper. DRY plus
  magic-number removal in one.
- **`RabbitMqBus`** extracts `NetworkRecoveryInterval = TimeSpan.FromSeconds(10)` as
  `static readonly`; the two duplicate `factory.NetworkRecoveryInterval =
  TimeSpan.FromSeconds(10)` setter sites now share the constant.
- **`SerilogExtensions.CleanupDevelopmentLogs`** replaces the unspecific `catch { }`
  with explicit `catch (IOException)` and `catch (UnauthorizedAccessException)` —
  best-effort rationale unchanged, but narrowed.

### Changed — doc polish

- **`README.md` (root):** the `Stratara.SourceGenerators` row is removed from the
  package map; the `Stratara.Identity.AspNetCore` description is corrected from
  `"ASP.NET Core / Blazor server-side identity"` to `"Channel-agnostic ASP.NET Core
  identity wiring (sign-in manager wrapper + i18n + email-sender stub)"`.
- **`src/Stratara.Outbox.RabbitMQ/README.md`:** stale
  `Stratara.EventSourcing.EntityFrameworkCore` `ProjectReference` claim removed from
  the dependency block (the package only references `Abstractions`, `Contracts`,
  `Mediator`, `Sessions`, `Shared`); clearer "runs alongside the EFCore package" note
  added.
- **`src/Stratara.ServiceDefaults/README.md`:** new "Prerelease dependencies" note
  documents the transitive `OpenTelemetry.Instrumentation.EntityFrameworkCore` (beta)
  and `RabbitMQ.Client.OpenTelemetry` (RC) references and the reason for the NU5104
  suppression.

### Changed — file renames (filename/class mismatch)

- `src/Stratara.Shared/Reflections/PropertyAccessCache.cs` →
  `PropertyAccessorCache.cs` (class is named `PropertyAccessorCache`).
- `src/Stratara.Outbox.RabbitMQ/DependencyInjection/MediatorServiceCollectionExtensions.cs`
  → `MediatorWorkerServiceCollectionExtensions.cs` (class is named
  `MediatorWorkerServiceCollectionExtensions`).

### Security disclaimer

- **HMAC bus-envelope signing covers identity only, not payload.**
  `BusEnvelopeCanonical.Of(CommandEnvelope)` covers `CommandTypeName + "|" +
  SessionContextJson`; `Of(EventBundle)` covers `SessionContextJson`. Properties
  marked with `[EncryptData]` are protected against swap via AAD binding; unencrypted
  properties are not. This is intentional (threat model: publish credentials are
  trusted), but adopters who assume HMAC integrity defends payload tampering are
  mistaken. The XML docs for `BusEnvelopeIntegrityOptions` will be sharpened in a
  follow-up release.

### Test coverage

- **Two new tests in `BusEnvelopeIntegrityStartupProbeTests`:** Permissive/Strict
  without signer → warning containing `"IBusEnvelopeSigner"`. Existing
  Permissive/Strict paths extended to cover "with signer" (using
  `Mock<IBusEnvelopeSigner>.Object`).
- **RabbitMqBus integration tests** updated for the new `IHostEnvironment` parameter:
  a small `TestHostEnv` stub (Development environment) in the test file; all six
  `new RabbitMqBus(...)` call sites extended.
- **`CommandAuditServiceCollectionExtensionsTests`** `using
  Microsoft.Extensions.Hosting;` → `using
  Microsoft.Extensions.DependencyInjection;` because of the namespace move.

## [3.0.13] — 2026-05-25

**Patch — managed-identity DI for Azure Service Bus, cleaner length check in
`HmacBusEnvelopeSigner.Verify`, production warning for `BusEnvelopeIntegrity.Mode=Off`.**
Three defence-in-depth polish items. No API break, no behavioural break.

### Added — managed-identity helper for Azure Service Bus

- **New extension `AddAzureServiceBusWithManagedIdentity(this IServiceCollection,
  string fullyQualifiedNamespace, TokenCredential? credential = null)`** in
  `Stratara.Outbox.AzureServiceBus`. Registers `ServiceBusClient` with the
  `(fullyQualifiedNamespace, TokenCredential)` overload, defaulting to
  `DefaultAzureCredential` (managed identity → environment → CLI chain). Plus a
  conventional `AddAzureServiceBus(string connectionString)` extension for the SAS
  variant (dev). Previously there was no first-class extension for AAD auth and the
  README pushed SAS — managed identity has a tight exposure surface plus automatic
  rotation/revocation and is the preferred production path.
- **`Stratara.Outbox.AzureServiceBus`** now additionally references
  `Azure.Identity` and `Microsoft.Extensions.DependencyInjection.Abstractions`.

### Changed — `HmacBusEnvelopeSigner.Verify` returns clean `false` on length mismatch

- **`HmacBusEnvelopeSigner.Verify` now checks Base64-byte length equality before
  calling `CryptographicOperations.FixedTimeEquals`.** Previously the wrapper threw
  `ArgumentException` as soon as an attacker sent a signature with a length other than
  44 chars — the dispatcher propagated it fail-closed but as an exception instead of
  clean `false`, which polluted logs and dashboards. Behaviour at the trust boundary
  is unchanged (length mismatch = invalid).

### Added — production warning for `BusEnvelopeIntegrity.Mode=Off`

- **New hosted service `BusEnvelopeIntegrityStartupProbe`** in
  `Stratara.Infrastructure.Security.Integrity`, registered via
  `AddBusEnvelopeIntegrity(...)`. Logs a `LogWarning` (EventId
  `LogEvents.BusEnvelopeIntegrity.IntegrityOffInProduction` = `113_001`) at host start
  whenever `BusEnvelopeIntegrityOptions.Mode == Off &&
  IHostEnvironment.IsProduction()`. **The default mode stays `Off` for backward
  compatibility** — flipping the default to `Permissive` is behaviourally breaking
  and is scheduled for 4.0.
- **New `LogEvents.BusEnvelopeIntegrity` bucket** (113_000 range) for integrity
  lifecycle events.

### Test coverage

- Two new `[InlineData]` cases in
  `Stratara.Infrastructure.Tests/Security/HmacBusEnvelopeSignerTests.cs`
  (`Verify_LengthMismatch_ReturnsFalse_WithoutThrowing`) — too-short and too-long
  signatures both return `false` without `ArgumentException`.
- Five new tests in
  `Stratara.Infrastructure.Tests/Security/BusEnvelopeIntegrityStartupProbeTests.cs`
  (Off+Production → warning; Off+Development → silent; Permissive+Production →
  silent; Strict+Production → silent; `StopAsync` clean).

## [3.0.12] — 2026-05-25

**Patch — PII redaction in the change-set log, FIFO eviction in `BackgroundTaskQueue`,
tracked cleanup for RabbitMQ subscriptions.** Three defence-in-depth polish items
bundled together because they touch independent code paths and none break the API.

### Security — no more `[EncryptData]` plaintext in `LogChangeSetCreated`

- **`LoggerChangeSetExtensions.LogChangeSetCreated` signature is now
  `(ILogger, Guid aggregateId, int changeCount, string fieldNames)`** instead of
  `(ILogger, Guid aggregateId, IReadOnlyList<ChangeDetail>)` with Serilog
  `{@ChangeSet}` destructuring. A source-compatible convenience overload preserves
  existing call sites (`logger.LogChangeSetCreated(aggregateId, changeSet)`) — it
  joins the `PropertyName`s internally and calls the source-generated method. The
  `ChangeDetail.SourceValue` / `CurrentValue` / `ChangeValue` fields (typed `object?`,
  may carry `[EncryptData]` plaintext) never reach the log line.

### Changed — FIFO eviction in `BackgroundTaskQueue`

- **`BackgroundTaskQueue._taskInfos` now evicts FIFO** as soon as `Count >
  maxRetainedTaskInfos` (default `10_000`). A new constructor overload
  `BackgroundTaskQueue(int capacity, int maxRetainedTaskInfos)` allows explicit cap
  configuration. Previously the dictionary grew unbounded: each `QueueTaskAsync`
  added an entry, nothing ever removed one → a long-running host or an authenticated
  spam loop could OOM. Minor behavioural change: lookups of TaskInfos older than the
  cap return `null` after eviction.

### Changed — RabbitMQ subscription cleanup tasks tracked and drained at shutdown

- **`RabbitMqBus.SubscribeAsync` now tracks each cancellation-token cleanup
  `Task.Run` in a `ConcurrentBag<Task>`.** `DisposeAsync` awaits those tasks
  (`Task.WhenAll(_cleanupTasks)`) before disposing the publish channel. Previously
  the cleanup was fire-and-forget — shutdown could race with an in-flight
  `ReceivedAsync` handler, dropping messages or surfacing secondary exceptions to
  the host. Liveness fix, no confidentiality risk.

### Test coverage

- One new test in
  `Stratara.Projections.Tests/Diagnostics/LoggerChangeSetExtensionsTests.cs`:
  `LogChangeSetCreated_DoesNotIncludeValuesInLogMessage` — verifies that a
  `"TOP-SECRET-PII"` plaintext value does NOT appear in the formatted log message,
  while field names do.
- Three new tests in
  `Stratara.Infrastructure.Tests/BackgroundTasks/BackgroundTaskQueueTests.cs`:
  constructor guard for `maxRetainedTaskInfos=0`, FIFO eviction when the cap is
  exceeded, and retention below the cap.

## [3.0.11] — 2026-05-25

**Patch — `DummyKeyStore` guard switched to a Development whitelist, plus startup
warning when the dummy is active.** Closes a plaintext-recovery hole on Staging, QA,
UAT, and Preview hosts. Previously only `if (environment.IsProduction()) throw`
protected, so every other environment name slipped through and encrypted with the
publicly-known pass-phrase `"StrataraTestKey"` shipped in the NuGet. Real-world
setups with production-data copies on Staging were unprotected.

### Changed — `DummyKeyStore` Development whitelist

- **`DummyKeyStore` now throws `InvalidOperationException` in every environment whose
  name is not exactly `Development`.** Whitelist instead of blacklist. The exception
  message shows the current `EnvironmentName` and includes the composition-root code
  pattern for the DI ordering so the operator can copy the right fix immediately.
- **`KeyStoreStartupProbe` now injects `ILogger<KeyStoreStartupProbe>`** and logs a
  `LogWarning` (EventId `LogEvents.KeyManagement.DummyKeyStoreActive` = `112_001`) at
  host start whenever the resolved `IKeyStore` implementation is `DummyKeyStore` —
  even in Development. An unintended dependency on the dummy becomes loud instead of
  silent.
- **`Stratara.Infrastructure/README.md`** has a new security section with composition-
  root examples for Azure Key Vault / AWS KMS / HSM backends.
- **New `LogEvents.KeyManagement` bucket** (112_000 range) for key-management
  lifecycle events.

**Behaviourally breaking for consumers running on non-Production hosts without an
explicit `AddSingleton<IKeyStore, ...>`** — treated as a CVE-class security fix and
shipped as a patch with this migration note:

```csharp
// Composition root (Program.cs)
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSecurity();   // DummyKeyStore fallback is OK
}
else
{
    // BEFORE AddSecurity() — TryAddSingleton won't overwrite
    builder.Services.AddSingleton<IKeyStore, AzureKeyVaultKeyStore>();  // or AwsKmsKeyStore, ...
    builder.Services.AddSecurity();
}
```

Hosts on Staging/QA/UAT that today run without a real KeyStore registered will crash
after the upgrade at DI-build time with a clear migration message in the exception —
exactly the point: they should NOT start without a real KeyStore.

### Test coverage

- Six new / updated tests in
  `Stratara.Infrastructure.Tests/Security/DummyKeyStoreTests.cs` (Production still
  throws, plus `[Theory]` over `Staging`/`QA`/`UAT`/`Preview`/`Stage`/`Test` — all now
  throw; Development still OK; exception message contains EnvironmentName and
  AddSecurity hint).
- Two new tests in
  `Stratara.Infrastructure.Tests/Security/KeyStoreStartupProbeTests.cs` (warning on
  DummyKeyStore, no warning on real KeyStore), plus three existing tests migrated to
  the new logger constructor.

## [3.0.10] — 2026-05-25

**Patch — `SessionContextMiddleware` header-tenant hop is opt-in.** Closes an
authenticated tenant-hopping hole. Previously the middleware silently fell back to
the `X-Tenant-Id` HTTP header as soon as the `stratara:tenant_id` JWT claim was
missing or unparsable. In a host issuing JWTs without a tenant claim, any
authenticated user could choose the effective tenant arbitrarily (propagating into
query filters, the AAD for crypto, and event-stream `TenantId`).

### Changed — `X-Tenant-Id` header is opt-in

- **New `SessionContextOptions.AllowTenantHeader` option** (default `false`) in
  `Stratara.Abstractions.Session`. Binds from the configuration section
  `"SessionContext"` or programmatically via
  `services.Configure<SessionContextOptions>(o => o.AllowTenantHeader = true)`.
- **`SessionContextMiddleware`** now injects `IOptions<SessionContextOptions>` and
  respects the option: with `false` (default), a missing or unparsable tenant claim
  falls back directly to `DefaultTenantIdentifier.Value` **without** touching the
  `X-Tenant-Id` header. With `true`, the old behaviour is preserved (header wins
  after claim).
- **`AddSessionContext()`** now also calls `services.AddOptions<SessionContextOptions>()`
  so the option resolves with default `false` without further configuration.

**Behaviourally breaking for consumers that today use the `X-Tenant-Id` header for
authenticated requests** — treated as a CVE-class security fix and shipped as a patch
with this migration note:

```csharp
// Option A (correct): put TenantId into the JWT claim (recommended)
claims.Add(new Claim(StrataraClaimTypes.TenantId, tenantId.ToString()));

// Option B: keep using the header, opt in
builder.Services.Configure<SessionContextOptions>(o => o.AllowTenantHeader = true);
```

### Test coverage

- 11 existing `SessionContextMiddlewareTests` migrated to the new constructor
  parameter.
- Two new tests: `Header_AllowedByOptions_FallsBackToHeader` (opt-in path) and
  `Header_NotAllowedByOptions_UsesDefault` (fail-closed default).

## [3.0.9] — 2026-05-25

**Patch — event-path integrity and JSON guards in `ProjectionWorker` and
`SagaWorker`.** Closes an asymmetry between `MediatorCommandWorker` (which verified
the HMAC signature on the command path) and the two event-bundle consumers (which did
**not**). With that gap, anyone with bus-publish rights (a compromised RabbitMQ user,
a leaked Service Bus connection string, a malicious co-tenant) could inject arbitrary
`EventBundle`s with forged `SessionContextJson` → cross-tenant ReadStore corruption
plus saga hijacking. The bundle also pulls the DoS guards
(`BusEnvelopeJsonGuard.EnsureWithinSizeLimit` plus `MaxDepth`) onto the
projection/saga path, matching what the command path already had.

### Fixed — `EventBundle` integrity verification on the event path

- **`ProjectionWorker` and `SagaWorker` now verify the HMAC signature of each inbound
  `EventBundle`** when an `IBusEnvelopeSigner` is registered and
  `BusEnvelopeIntegrityOptions.Mode` is not `Off`. Identical Off/Permissive/Strict
  mode semantics as `MediatorCommandWorker`. **Behaviour extension on the failure
  path only** — no API break, no default change (mode default stays `Off`).
  Consumers without an `AddBusEnvelopeIntegrity(...)` call see zero behavioural
  difference.
- **New tier-A helper `BusEnvelopeIntegrityVerifier.Verify(...)`** in
  `Stratara.Abstractions.Messaging`, plus result enum `BusEnvelopeIntegrityResult`
  (`Skipped` / `Verified` / `RejectedPermissive` / `RejectedStrict`). Centralises the
  Off/Permissive/Strict decision so all three workers (mediator-command, projection,
  saga) share identical semantics. `MediatorCommandWorker.VerifyEnvelopeIntegrity` is
  refactored onto the new helper — behaviour-neutral, all 44 existing
  Outbox.RabbitMQ tests stay green.
- **New logger extensions `LoggerEventBundleExtensions`** in
  `Stratara.Shared.Diagnostics.Extensions` (`LogEventBundleIntegrityWarning` /
  `LogEventBundleIntegrityRejected`); EventID schema bucket
  `LogEvents.EventBundleIntegrity` (111_000 range) added. Bundles are identified by
  `(firstEventId, eventCount)` (`EventBundle` has no own Id field by design — the
  contained events carry their own ids).

### Added — DoS guards for SessionContextJson on the event path

- **`ProjectionWorker` and `SagaWorker` now call
  `BusEnvelopeJsonGuard.EnsureWithinSizeLimit(...)` against the `SessionContextJson`
  byte count** (default 1 MiB via `BusEnvelopeJsonOptions.MaxBodyBytes`) and route
  deserialisation through `BusEnvelopeJsonGuard.CreateOptions(MaxDepth)` (default
  32). A hostile publisher can no longer OOM or stack-overflow the worker via
  oversized or deeply-nested session JSON. Identical limits to the command path
  (`MediatorCommandWorker` already had them).

### Test coverage

- Six new tests in
  `Stratara.Shared.Tests/Messaging/BusEnvelopeIntegrityVerifierTests.cs` (Off /
  no-signer / verified / Permissive-reject / Strict-reject / null-signature).
- Six new tests in
  `Stratara.Projections.Tests/Services/ProjectionWorkerIntegrityTests.cs` (Off,
  Strict-valid, Strict-missing-throws, Strict-tampered-throws,
  Permissive-missing-warns-and-dispatches, size-limit-exceeded-throws).
- Six new tests in `Stratara.Sagas.Tests/Services/SagaWorkerIntegrityTests.cs`
  (analogous).

## [3.0.8] — 2026-05-25

**Patch — `RabbitMqBus.PublishAsync` no longer loses `EventBundle`s when no
subscriber is bound yet.** Fixes a silent data-loss bug during the startup race,
subscriber outage, or rolling-upgrade window. Previously `RabbitMqBus` published with
`mandatory=false` — RabbitMQ default for fanout exchanges: without a bound queue,
the message is silently dropped and the publisher gets no signal.
`EventBundleOutboxDispatcher` and `CommandOutboxDispatcher` interpreted the publish
confirm as success and did NOT write an outbox fallback. Discovered in production by
a downstream consumer ("Wait for database seeding" timeouts because the
`EventProjection` worker bound its queue two seconds after the publish of the
seeding event bundle).

### Fixed — RabbitMQ fanout publish race

- **`RabbitMqBus.PublishAsync` sets `mandatory=true`.** Combined with the existing
  publisher-confirm tracking configuration
  (`CreateChannelOptions(publisherConfirmationsEnabled: true,
  publisherConfirmationTrackingEnabled: true, …)`), `BasicPublishAsync` now throws
  `PublishReturnException` as soon as the broker returns the message because of
  missing bindings. The existing `try/catch` fallbacks in
  `EventBundleOutboxDispatcher.TrySendEventBundleAsync` and
  `CommandOutboxDispatcher.TrySendCommandEnvelopeAsync` catch it and write the
  bundle/envelope to the outbox table; `OutboxWorker` re-publishes on the next poll
  cycle (default 5 s) until a subscriber is bound. Pure behavioural change on the
  failure path — no API break. Consumer-side workarounds waiting for a projection
  before continuing can be removed after adopting this release. Test coverage: new
  integration test `PublishAsync_NoSubscriberBound_ThrowsPublishReturnException` in
  `Stratara.Outbox.RabbitMQ.IntegrationTests/Messaging/RabbitMqBusTests.cs`
  (Testcontainers-RabbitMQ, fanout exchange without binding, asserts
  `PublishException.IsReturn == true`).

## [3.0.7] — 2026-05-24

**Patch — `AddAggregatesFromAssemblyContaining<T>` now also registers Apply-method
event parameters in the trusted-type resolver.** Closes a gap discovered in
production by a downstream consumer:
`IAggregationService.AggregateAsync<Tenant>(...)` broke with
`InvalidOperationException: Type 'Stratara.Domain.TenantCreated, …' is not registered
in the trusted-type resolver`, even though `AddAggregatesFromAssemblyContaining<Tenant>()`
was registered. The diagnostic message explicitly named
`AddAggregatesFromAssemblyContaining<T>` as a sufficient registration — the actual
behaviour contradicted its own self-description. If Stratara offers a registration
API, it must register everything needed for aggregate replay.

### Fixed — trusted-type resolver gap for aggregate events

- **`AddAggregatesFromAssemblyContaining<T>()`** now scans, in addition to the
  `IAggregate` implementations, every public instance `Apply(...)` method with
  exactly one parameter and registers the parameter type in the
  `ITrustedTypeResolver`. Pure extension — no API break, no behavioural break for
  existing consumers (who additionally call `AddTrustedType<TEvent>()`). Test
  coverage: two new tests in `Stratara.Shared.Tests/Reflections/` (Apply-parameter
  registration; multi-parameter and parameterless Apply overloads are ignored).

## [3.0.6] — 2026-05-24

**Patch — `AuthorizationException` middleware extension.** Closes a consumer gap
from the 3.0.2 demote wave. `AuthorizationExceptionMiddleware` was correctly demoted
to `internal sealed` in 3.0.2, but `Stratara.Infrastructure` did not ship a
`UseAuthorizationExceptionTo403()` application-builder extension. Consumers that had
been calling `app.UseMiddleware<AuthorizationExceptionMiddleware>()` directly broke
with `CS0122` (inaccessible).

### Added

- **`UseAuthorizationExceptionTo403()` application-builder extension** in
  `Stratara.Infrastructure` (namespace `Microsoft.Extensions.Hosting`, consistent
  with the `IServiceCollection` extensions in the same package). Hooks
  `AuthorizationExceptionMiddleware` into the request pipeline and translates any
  downstream `AuthorizationException` into HTTP 403 (`Forbidden`); other exceptions
  propagate to the global exception handler. Usage:

  ```csharp
  app.UseAuthentication();
  app.UseAuthorization();
  app.UseAuthorizationExceptionTo403();
  ```

  Test coverage: five new tests in
  `Stratara.Infrastructure.Tests/DependencyInjection/` (403 on AuthZ exception,
  pass-through without exception, rethrow other exceptions, null guard, chaining
  return) — using a real `ApplicationBuilder` plus `DefaultHttpContext` without a
  `Microsoft.AspNetCore.TestHost` dependency.

## [3.0.5] — 2026-05-24

**Patch — `CommandAudit` registration extension.** Closes a consumer gap from the
3.0.2 demote wave: `CommandAuditBehavior<TRequest, TResult>` and
`CommandAuditBehavior<TRequest>` were correctly demoted to `internal sealed` in
3.0.2, but the `Stratara.EventSourcing.Pipeline.CommandAudit` package did not ship a
DI extension for registration. Consumers that had been calling
`AddPipelineBehaviorWithResult(typeof(CommandAuditBehavior<,>))` directly broke with
`CS0122` (inaccessible).

### Added

- **`AddCommandAuditing()` service-collection extension** in
  `Stratara.EventSourcing.Pipeline.CommandAudit` (namespace
  `Microsoft.Extensions.Hosting`, convention identical to `AddMediator`,
  `AddEventSourcing`, `AddSecurity`, etc.). Registers both behavior arities as
  `Scoped` against the open-generic `IPipelineBehavior<,>` / `IPipelineBehavior<>`
  service types. Replaces the `typeof(CommandAuditBehavior<,>)` direct reference
  from consumer code, which is no longer possible since the 3.0.2 demote. Usage:

  ```csharp
  builder.Services
      .AddMediator()
      .AddCommandAuditing();
  ```

  Test coverage: three new tests in
  `Stratara.EventSourcing.Pipeline.CommandAudit.Tests/DependencyInjection/` (both
  arities registered, chaining return, null guard).

## [3.0.4] — 2026-05-24

**Patch — code-style consistency.** Pure internal code-style cleanup — no API
surface impact, no behavioural change for consumers.

### Fixed — `ConfigureAwait` policy drift

- **44 `.ConfigureAwait(false)` calls** removed from the source code (12 files
  across `Stratara.Outbox.RabbitMQ`,
  `Stratara.EventSourcing.Pipeline.CommandAudit`,
  `Stratara.EventSourcing.EntityFrameworkCore`, `Stratara.Infrastructure`,
  `Stratara.Outbox.AzureServiceBus`). The code now reflects the documented policy
  ("NOT used in Stratara — the framework assumes ASP.NET / Generic Host"); on
  .NET 10 + ASP.NET Core / Generic Host, `ConfigureAwait(false)` is a no-op (no
  `SynchronizationContext` to capture). Pure internal code-style consistency, no
  API surface impact.

## [3.0.3] — 2026-05-24

**Patch — `EntityModeling` read-models removed, plus SonarQube coverage tooling on
MTP-native.** Last pre-public surface-freeze step: 14 EntityModeling read-models
plus accompanying surface removed from the framework package (verified to have no
active consumer use). Plus the SonarQube pipeline switched to MTP-native coverage
reading.

### Removed (BREAKING) — `EntityModeling` read-models

- **`Stratara.Projections.EntityModeling.**` removed completely** — 14 read-model
  types (`AttributeDefinition`, `AttributeDefinitionGroup`,
  `AttributeGroupAssignment`, `AttributeValue`, `EntityExtension`, `EntityGroup`,
  `EntityGroupAssignment`, `EntityType`, `StaticFieldUiBinding`,
  `UiComponentDefinition`, `LookupEntry`, `LookupType`, `TranslationEntry`) plus
  the `DataType` enum and the associated `AttributeDefinitionConfiguration` in
  `Stratara.EventSourcing.EntityFrameworkCore.ReadStore.EntityModeling`. These
  were leftovers from the first Stratara draft in 2024, application-specific EAV
  read-models that don't belong in an application-agnostic framework.
- **`IHasEntityExtension` marker plus `EntityExtensionId` property** removed from
  `Stratara.Abstractions.Entities` — referenced only by the EntityModeling types
  above, no other framework consumer.

**Migration (consumers):** consumers that derive from
`Stratara.EventSourcing.EntityFrameworkCore.ReadStore.ReadDbContext<T>` lose the
EntityModeling tables (`attribute_definition`, `attribute_definition_group`,
`attribute_group_assignment`, `attribute_value`, `entity_extension`,
`entity_group`, `entity_group_assignment`, `entity_type`,
`static_field_ui_binding`, `ui_component_definition`, `lookup_entry`,
`lookup_type`, `translation_entry`) from the EFCore model. After adoption, run
`dotnet ef migrations add Drop_StrataraEntityModelingTables` to generate a drop
migration against the existing database.

### Added (tests — coverage over 80%)

- **DI-extension coverage:** unit tests added for the previously untested DI
  helpers so SonarQube's overall coverage lands above 80% (locally 81.83% vs.
  79.66% before, +75 covered lines):
  - `IntegrityServiceCollectionExtensionsTests` — `AddBusEnvelopeIntegrity(Action)`
    and `AddBusEnvelopeIntegrity(IConfiguration)` overloads (signer + options
    binding + null guards).
  - `CachingServiceCollectionExtensionsTests` — `AddCaching` missing-connection-
    string throw and singleton registration via `IConnectionMultiplexer` factory.
  - `TrustedTypeResolverServiceCollectionExtensionsTests` —
    `AddTrustedTypeResolver` idempotency, `AddTrustedType<T>`,
    `AddAggregatesFromAssemblyContaining<T>` aggregate scan including
    abstract/interface skip.
  - `ProjectionServiceCollectionExtensionsTests` — `AddProjectionWorker`
    manager/handler/invoker plus `ProjectionWorker`/`ReplayWorker` hosted-service
    registrations; `AddProjectionsFromAssemblyContaining<T>` scan.
  - `TypeExtensionsTests` — `GetQualifiedTypeName` caching plus
    `GetVersionIndependentTypeName` for unqualified, single-comma, multi-comma,
    and trimmed inputs.

### Fixed (SonarQube coverage tooling)

- **MTP-native code coverage** replaces the broken `coverlet.console` wrapping in
  `azure-pipelines-sonarqube.yml`. Coverlet's assembly-load hooks didn't reach
  through Microsoft.Testing.Platform's `AssemblyLoadContext` → every nightly
  produced 0%-coverage XMLs for all modules. The new setup centralises
  `Microsoft.Testing.Extensions.CodeCoverage` (18.0.6 — last version on
  Platform v1, compatible with xunit.v3 3.2.2's mtp-v1 integration) in
  `tests/Directory.Build.targets`; the pipeline calls
  `dotnet test ... -- --coverage --coverage-output-format xml` per test project.
  SonarQube now reads VS-coverage XML via
  `sonar.cs.vscoveragexml.reportsPaths`.

### Fixed (SonarQube nightly findings)

- **S2699 (BLOCKER)** — `BusEnvelopeJsonGuardTests.EnsureWithinSizeLimit_BelowLimit_DoesNotThrow`
  and `_AtLimit_DoesNotThrow` gain explicit `Record.Exception` + `Assert.Null`
  assertions instead of relying on "no throw == pass".
- **S2365 (CRITICAL)** — `TrustedTypeResolver.RegisteredTypes` annotated with
  `[SuppressMessage]` plus justification; the snapshot semantics are documented on
  `ITrustedTypeResolver` and the sole consumer (`EncryptionMetadataDriftGuard`)
  runs at host start, not on the hot path.
- **S3011 (MAJOR, ×2)** — `SagaServiceCollectionExtensions.RegisterHandledEventTypes`
  and `ProjectionServiceCollectionExtensions.RegisterHandledEventTypes` annotated
  with `[SuppressMessage]` plus justification; `BindingFlags.NonPublic` is needed
  because saga and projection handlers may declare `HandleAsync` non-public.
- **S108 (MAJOR)** — `ProjectionReplayWorker.ExecuteAsync`'s empty
  `catch (OperationCanceledException)` gains an explanatory comment (host shutdown
  is not a replay failure).
- **S1144 (MAJOR)** — unused `NotAllowedKey` field and the associated
  `Identity.SignIn.NotAllowed` resource entry (en + de) removed from
  `AspNetSignInManager`.
- **S107 (MAJOR, ×2)** — `MediatorCommandWorker` (10 params) and
  `CommandOutboxDispatcher` (9 params) annotated with `[SuppressMessage]` plus
  justification; both are DI-resolved `internal sealed` workers, the parameter
  counts mirror intrinsic framework dependencies and are not hand-called API
  surface.
- **S3267 (MINOR)** — `AuthorizationStartupValidator.FindRoleProtectedTypes` inner
  loop simplified to a `.Where(...)` LINQ expression.
- **CA1873 (INFO, ×2)** — `ProjectionManager.RunProjectionIfRelevant` and
  `SagaManager.RunSagaIfRelevant` "events not relevant" debug log wrapped with a
  `logger.IsEnabled(LogLevel.Debug)` guard so `string.Join`/`Distinct` aren't
  evaluated when the channel is disabled.
- **CA1822 (INFO, ×2)** — `tests/Directory.Build.props` NoWarn list extended with
  CA1822. Test fakes for projection/saga DI scans intentionally use instance
  `HandleAsync` methods (for the `BindingFlags.NonPublic` reflection search);
  CA1822 reported them as "could be static". The pipeline configuration
  `sonar.issue.ignore.multicriteria.staticMethodsInTests.resourceKey=tests/**`
  doesn't reliably catch external Roslyn issues — NoWarn at the compile level
  closes the gap deterministically.

## [3.0.2] — 2026-05-23

**Audit closeout.** A post-3.0.1 sanity-check audit closed 27 of 29 findings
(5 HIGH / 12 MEDIUM / 10 LOW). This bump brings the API-surface freeze
(sealing, internal demotion, name renames), opt-in bus-envelope integrity, JSON
limits, an `EncryptData` drift guard, PII logger cleanup, and a documentation
sweep.

### Added (security)

- **Opt-in HMAC integrity protection** for bus envelopes. New tier-A surface in
  `Stratara.Abstractions.Messaging`: `IBusEnvelopeSigner`,
  `BusEnvelopeIntegrityOptions` (`Mode` Off/Permissive/Strict + `SharedKey`),
  `BusEnvelopeCanonical`. Default implementation `HmacBusEnvelopeSigner` in
  `Stratara.Infrastructure`. DI: `services.AddBusEnvelopeIntegrity(...)`.
  `CommandEnvelope` and `EventBundle` now carry an optional `Signature` field
  (JSON-additive, default `null` → no behaviour without opt-in).
  `CommandOutboxDispatcher` and `EventSource.PublishEventBundleAsync` sign when a
  signer is registered; `MediatorCommandWorker.DispatchAsync` verifies.
  `SECURITY.md` extended with a "Trust boundaries / Message bus" section
  including threat model and roll-out recommendation.
- **JSON deserialisation limits** on the bus boundary. New tier-A class
  `BusEnvelopeJsonOptions` (`MaxDepth=32`, `MaxBodyBytes=1 MiB`, section
  `BusEnvelopeJson`) plus `BusEnvelopeJsonGuard`. `RabbitMqBus.SubscribeAsync`,
  `AzureServiceBusBus.SubscribeAsync`, and `MediatorCommandWorker.DispatchAsync`
  enforce before every `JsonSerializer.Deserialize`.
- **`IAuthorizingMediator` marker** in `Stratara.Abstractions.Mediator`.
  `AuthorizingMediator` implements it; `AuthorizationStartupValidator` now checks
  against the runtime `IMediator` object, not the DI container — catches custom
  decorator setups and open-generic providers that the previous probe missed.
- **`EncryptionMetadataDriftGuard`** — new `IHostedService` in
  `Stratara.Infrastructure.Security.Serialization` that scans the trusted-type
  allowlist at start and fails when `EncryptionMetadata.RequiresEncryption` and
  the actual `[EncryptData]` presence diverge. Defence-in-depth for future
  attribute branches.
- **`ITrustedTypeResolver.RegisteredTypes`** — additive snapshot accessor on the
  tier-A interface, consumed by the drift guard.

### Changed (BREAKING) — pre-public API surface freeze

- **Records `sealed`** before the first public nuget.org push: `EventMessage`,
  `EventBundle`, `CommandEnvelope`, `SessionContext`, `PagedRequest`
  (`Stratara.Contracts`); `ClaimsResponse`, `ClaimDto` (`Stratara.Identity.Core`);
  `Event<TEvent>` (`Stratara.Shared`).
- **`Stratara.ServiceDefaults.AspNetCore.OpenTelemetryExtensions` →
  `AspNetCoreOpenTelemetryExtensions`**. The name collided with the eponymous
  class in `Stratara.ServiceDefaults` and triggered a CS0433 build break under
  documented happy-path adoption (lean ServiceDefaults + AspNetCore overlay).
- **`Stratara.EventSourcing.WorkerDefaults.ServiceCollectionExtensions` →
  `WorkerDefaultsHostBuilderExtensions`**. The generic class name was prone to
  collision with consumer-infrastructure helpers in the same
  `Microsoft.Extensions.Hosting` namespace.
- **EF Core implementation surface to `internal sealed`**: repositories (6),
  `DefaultDbResolver`, `GuidV7ValueGenerator`, `ByteArrayToUIntConverter`,
  `CommandLogEntry`, all seven `*Configuration` classes. Constructor signatures
  are no longer part of the SemVer contract. Carve-out: `WriteDbContext<TContext>`,
  `ReadDbContext<TContext>`, `IdentityStore<TContext, TUser>`,
  `WriteUnitOfWork<TDbContext>`, `ReadUnitOfWork<TDbContext>`,
  `ProjectionsUnitOfWork<TDbContext>` stay `public class` because consumers
  subclass them.
- **Saga / projection / outbox / infrastructure implementations to
  `internal sealed`**: `MediatorCommandWorker`, `CommandOutboxDispatcher`,
  `EventBundleOutboxDispatcher`, `RabbitMqBus`, `AzureServiceBusBus`,
  `OutboxWorker`, `NullOutboxLock`, `RedisOutboxLock`, `ProjectionReplayState`,
  all projection services (`ProjectionManager`, `ProjectionHandler`,
  `ProjectionMethodInvoker`, `ProjectionWorker`, `ProjectionReplayWorker`), all
  saga services (`SagaManager`, `SagaHandler`, `SagaMethodInvoker`,
  `SagaWorker`), `CommandAuditBehavior` (both arities), `BackgroundTaskQueue`,
  `QueuedHostedService`, `TenantService`, `CurrentUserService`, `DummyKeyStore`,
  `AesGcmEncryptionFactory`, `SecureBlobEncryptor`, `SecureJsonSerializer`,
  `AuthorizationExceptionMiddleware`. Interfaces (`ICommandOutboxDispatcher`,
  `IMessageBus`, `IProjection*`, `ISaga*`, …) remain `public`.
- **`Stratara.Sessions` reclassified from tier-A to tier-C**. It packs
  `Microsoft.AspNetCore.Http.Abstractions` and ships `SessionContextMiddleware`.
  No code move, documentation update only.

### Changed (logging — polish)

- **PII logger cleanup**: `LogEventsNotRelevantForProjection` and
  `LogEventsNotRelevantForSaga` now accept `(int eventCount, string
  eventTypeNames, string …Name)` instead of `IReadOnlyList<IEvent>`.
  Structurally impossible to accidentally reintroduce `@{Events}` — no PII leak
  at Debug level in prod.

### Fixed

- **`MediatorCommandWorker.Dispose()`** override added — the `BucketLockPool`
  with 4096 `SemaphoreSlim`s is now disposed at host shutdown.
- **Async-void-in-disguise** in `RabbitMqBus.SubscribeAsync` replaced by a sync
  delegate plus reified `Task.Run` plus `DisposeAsync` (instead of `CloseAsync`).
- **Empty catch in publish-channel reset** replaced by a new source-gen
  `LogPublishChannelCleanupFailed` (108_109).
- **Dead null check and misleading XML doc** on
  `SecureJsonSerializer.DeserializeAsync<T>` removed.
- **`Stratara.Outbox.RabbitMQ` `ProjectReference` on
  `Stratara.EventSourcing.EntityFrameworkCore`** removed — it was unused and
  transitively pulled in tier-C coupling.

### Documentation

- **Root README** package map now shows all 21 packages (the
  `Stratara.Outbox.AzureServiceBus` row had been missing since 3.0.0).
- **CHANGELOG preamble** corrected to 21 packages.
- **Internal versioning and publish-pipeline docs** updated — examples on 3.x,
  stable releases table through v3.0.1, trigger pattern (`v*`), pre-release shape
  (3.0.x), and additional pipelines added.
- **`Stratara.Abstractions/README.md`** phantom-dependency claim on
  `Stratara.Domain` removed.
- **`IAuthorizationProvider`** gains an `<example>` block with an
  `HttpContext.User.IsInRole` implementation.
- **`<example>` blocks** added to further high-traffic entry points —
  `IEventSource`, `IAggregationService`, `ICommandOutboxDispatcher`,
  `AddMediator`, `AddCommandHandlersFromAssemblyContaining`,
  `AddQueryHandlersFromAssemblyContaining`, all six `Add*WorkerServices`
  composites plus `AddCommonFrameworkServices`, `AddMessaging`,
  `AzureServiceBusBus`, `AddProjectionsFromAssemblyContaining`,
  `AddSagasFromAssemblyContaining`. Quick-reference snippets added to the READMEs
  of `Stratara.Shared` and `Stratara.Domain`.

### Changed (tooling / pipeline prep)

- **`azure-pipelines-publish.yml`** prepared for `.snupkg` push against
  `symbols.nuget.org` — gated/commented-out, will be activated with the first
  public nuget.org push.

## [3.0.1] — 2026-05-23

**Patch release** — pulls forward the sync-API removal originally scheduled for
4.0. Was initially planned as a 3.0.0 recut but ran into Azure Artifacts package
immutability (`--skip-duplicate` plus versions immutable after publish) → patch
bump instead of recut.

### Removed (BREAKING)

- **Sync-over-async wrappers on `ISecureJsonSerializer`, `IUnitOfWork`,
  `ITransaction` deleted.** The four methods `Serialize<T>` / `Serialize` /
  `Deserialize<T>` / `Deserialize` on `ISecureJsonSerializer`, plus
  `IUnitOfWork.Start()` and `ITransaction.SaveChanges()`, no longer exist.
  Consumers call only the `*Async` variants. The sync implementations were
  `XxxAsync(...).GetAwaiter().GetResult()` — a deadlock pattern in
  single-threaded sync contexts (legacy ASP.NET, MAUI/WPF UI thread) and a
  threadpool-starvation source in ASP.NET Core. Migration is mechanical:
  `serializer.Serialize(x, t, u)` → `await serializer.SerializeAsync(x, t, u,
  ct)`; `uow.Start()` → `await uow.StartAsync(ct)`; `tx.SaveChanges()` →
  `await tx.SaveChangesAsync(ct)`.

### Added (tests)

- New test classes for `AuthorizationStartupValidator`, `AspNetSignInManager`,
  `KeyStoreStartupProbe`, and `EventChainRepository`. Aggregate line coverage
  rises from 77.8% to 80.2%.

## [3.0.0] — 2026-05-23

**Cross-cutting major release.** First nuget.org release candidate. Consumer
migration: one new package (`Stratara.Outbox.AzureServiceBus`), a mandatory
type-allowlist registration in the worker host, and several sign-in / mediator
behaviour adjustments.

### Added

- **New package `Stratara.Outbox.AzureServiceBus`** — the Azure Service Bus
  `IMessageBus` implementation now lives in its own package starting with 3.0
  (previously transitive in `Stratara.Outbox.RabbitMQ`). Class renamed:
  `Stratara.Outbox.RabbitMQ.Messaging.ServiceBus` →
  `Stratara.Outbox.AzureServiceBus.Messaging.AzureServiceBusBus`. Consumers
  without a Service Bus need save the ~5 MB `Azure.Messaging.ServiceBus`
  dependency.
- **`ITrustedTypeResolver`** (`Stratara.Abstractions.Reflections`) — per-host
  allowlist for CLR-type resolution out of message-bus envelopes, event-store
  rows, and snapshot rows. Auto-populated by
  `AddCommandHandlersFromAssemblyContaining<T>`,
  `AddProjectionsFromAssemblyContaining<T>`,
  `AddSagasFromAssemblyContaining<T>`, and the new
  `AddAggregatesFromAssemblyContaining<T>`. Explicitly registrable via
  `services.AddTrustedType<T>()`. Closes the untrusted-type-resolution attack
  surface.
- **`AuthorizationStartupValidator`** (`Stratara.Mediator.Authorization`) —
  `IHostedService` that at startup scans loaded assemblies for
  `[RequireRole]`-decorated types and fails with `InvalidOperationException`
  when `AddMediator()` was called but no `IAuthorizationProvider` is registered.
  Prevents `[RequireRole]` annotations from being silently ignored.
- **`KeyStoreStartupProbe`** (`Stratara.Infrastructure.Security.KeyManagement`)
  — `IHostedService` that resolves `IKeyStore` once at host start.
  `DummyKeyStore`'s `IsProduction()` guard now fires at boot rather than lazily
  on the first encryption code path.
- **`StrataraSignInResult.IsNotAllowed`** — new init-only property for
  trusted-context UIs. User-visible message stays `InvalidCredentialsKey`
  (username-enumeration protection).
- **`StrataraHeaderNames`** (`Stratara.Sessions`) — `public static class` with
  `TenantId = "X-Tenant-Id"` and `ClientId = "X-Client-Id"`. Consumers can
  reference these constants when sending requests to Stratara hosts.
- **`SECURITY.md`** at repo root — disclosure address
  (`security@stratara.tech`), 5-day acknowledgement / 30-day fix SLA, supported-
  versions matrix, scope definition.
- **`<example>` blocks** on flagship surfaces: `RequireRoleAttribute`,
  `EncryptDataAttribute`, `IMediator`.

### Changed (BREAKING)

- **Untrusted type resolution replaced with an allowlist** (security HIGH).
  - `MediatorCommandWorker` constructor signature: `(...,
    ResiliencePipelineProvider<string>)` → `(...,
    ResiliencePipelineProvider<string>, ITrustedTypeResolver,
    ISecureJsonSerializer)`.
  - `EventMapperFactory(ISecureJsonSerializer)` → `(ISecureJsonSerializer,
    ITrustedTypeResolver)`.
  - `SnapshotService(..., IWriteUnitOfWork)` → `(..., IWriteUnitOfWork,
    ITrustedTypeResolver)`.
  - Consumer migration: all three are resolved via DI; if the host composition
    uses `AddCommandHandlersFromAssemblyContaining<T>` plus the
    projection/saga/aggregate scan extensions, the resolver is auto-populated
    and no code change is needed. Hosts that instantiate `EventMapperFactory`
    directly (atypical — should go through DI) must supply the additional
    parameter.
- **`[EncryptData]` is now end-to-end on the command bus** (security HIGH).
  - `CommandEnvelopeMapper.MapTo<T>(this T, SessionContext)` →
    `MapToAsync<T>(this T, SessionContext, ISecureJsonSerializer,
    CancellationToken)`. Source-breaking for consumers that call the mapper
    directly (rare — happens internally in the framework). Command payloads
    with `[EncryptData]` properties no longer leak plaintext values into broker
    logs, DLQs, or outbox persistence.
  - `CommandOutboxDispatcher` constructor: `(..., IProjectionReplayState)` →
    `(..., IProjectionReplayState, ISecureJsonSerializer)`.
- **Service Bus package split** (see Added): any consumer that references the
  old `Stratara.Outbox.RabbitMQ.Messaging.ServiceBus` class must switch to
  `Stratara.Outbox.AzureServiceBus.Messaging.AzureServiceBusBus` and install
  the new NuGet package.
- **Nine concrete infrastructure implementations demoted from
  `public sealed` to `internal sealed`** (`EventSource`,
  `AggregationService`, `ChangeSetHandler`, `EventChainService`,
  `EventStreamHashService`, `EventStreamHashWorker`, `EventTypeResolver`,
  `SnapshotService`, `AuthorizingCommandOutboxDispatcher`). Consumers must
  resolve via the corresponding interface — `new EventSource(...)` etc. is no
  longer possible. Best practice before; now enforced by the compiler.
- **`AddCommonFrameworkServices`** is `public` instead of `private` (doc-vs-code
  drift corrected). Hosts that compose their own worker layouts can now call
  the extension directly.
- **Sync API on `ISecureJsonSerializer` / `IUnitOfWork` / `ITransaction` is
  `[Obsolete]`** with hint "Removed in 4.0". Compile warning on consumer use
  (no build break). Switch to the async variant is recommended. **Note (3.0.1):
  the actual removal was pulled forward to 3.0.1.**
- **`IEventMapperFactory.MapToEvents` → `MapToEventsAsync`** (async migration as
  a sync-API-removal consequence). Both overloads (stream entries + messages)
  take an optional `CancellationToken`; return type `Task<IReadOnlyList<IEvent>>`.
  Consumers replace `var events = factory.MapToEvents(entries)` with
  `var events = await factory.MapToEventsAsync(entries, ct)`.
- **Sign-in failure messages collapsed** (security MEDIUM).
  `AspNetSignInManager.PasswordSignInAsync` sets the user-visible message
  identically for `IsNotAllowed` and `InvalidCredentials` (previously two
  distinct i18n keys, enabling username enumeration). Trusted-context UIs can
  branch on `StrataraSignInResult.IsNotAllowed`.

### Changed (non-breaking)

- **`RabbitMqBus.PublishAsync` connection pool**: the singleton bus now caches a
  TCP connection plus channel (`SemaphoreSlim`-guarded) instead of building a
  new connection per publish. Per-message floor drops from 30-100 ms to <1 ms.
  `RabbitMqBus` is now `IAsyncDisposable`.
- **MessageBus pipeline circuit breaker**: 10 consecutive failures within 60 s
  open the breaker for 60 s. Unbounded retry behaviour (at-least-once via
  outbox) is preserved, but pathological loops (broken broker URL) now surface
  as discrete events instead of log storms.
- **`BucketLockPool` pre-allocated**: 4096 `SemaphoreSlim`s are created at
  construction; the acquire path is an indexed array read (instead of
  `ConcurrentDictionary` lookup plus lazy `SemaphoreSlim` allocation).
- **`SecureJsonSerializer` rejects null `TenantId`** (security MEDIUM):
  `BuildAdditionalAuthenticatedData` throws `InvalidOperationException` when
  `tenantId is null` instead of producing an AAD without tenant binding.
- **OpenTelemetry header redaction**: `RedactSensitiveHeaderTags` uses a `for`
  loop instead of LINQ `Where`, eliminating Gen0 garbage per HTTP request.
- **`EventStreamHashService.ComputeHash`** uses `IncrementalHash` plus
  `Utf8Formatter` plus stack `Span` instead of `StringBuilder` +
  `UTF8.GetBytes`. At BatchSize=10_000 this saves ~10-20 MB of Gen0 garbage per
  batch.
- **`BucketCalculator.GetBucketId`** uses `hash & int.MaxValue` instead of
  `Math.Abs(hash)` — `Math.Abs(int.MinValue)` would throw `OverflowException`
  and is reachable with non-zero probability from `Guid.GetHashCode()`.
- **`RabbitMqBus` connection-string credential leak**: malformed `amqp://`
  URIs are rethrown with a sanitised `InvalidOperationException` instead of
  letting the `UriFormatException` (with userinfo component) propagate into
  OpenTelemetry or logs.
- **`RabbitMqBus` guest/guest fallback warning**: `LogRabbitMqGuestFallback`
  (Event ID 108_108) fires when `RABBITMQ_USERNAME` / `RABBITMQ_PASSWORD`
  environment variables are missing and default credentials are used.
- **`EventChainRepository.GetLastSequenceNumberOrDefaultAsync`**: an empty
  `bucketIds` list now skips the `Where` predicate entirely instead of emitting
  always-true SQL.
- **Magic strings extracted**: `PostgresUniqueViolationSqlState = "23505"` as a
  `const` in `EventSource`; new `StrataraHeaderNames` (see above).
- **Root README** updated for 2.0.0/3.0.0: new §Status / §Install / §Migrating-
  from-1.x sections; FSL license disclosure at the top instead of the bottom.
- **License disclosure** as a top-of-README one-liner in every per-project
  README (20 packages).
- **`Stratara.Contracts` and `Stratara.Diagnostics` READMEs** each gain a
  three-line quick-reference block.
- **Narrative `//` comments removed from `src/`** (per the convention "no code
  comments in normal source"). Real engineering rationale lifted into XML
  `<remarks>` (e.g. `RabbitMqBus` 4.x exclusive-queue note).

## [2.0.0] — 2026-05-22

### Removed (BREAKING)

- **`Stratara.Identity.AspNetCore` split**: Blazor-specific code removed from
  the `Stratara.Identity.AspNetCore` package; the package is now channel-agnostic
  ASP.NET Core (no Blazor Server transitive, no WebAssembly.Server package
  dependency). Deleted:
  - `BlazorAuthenticationStateProvider<TUser>` (inherited from
    `RevalidatingServerAuthenticationStateProvider` — pure Blazor Server).
    Consumers on a Blazor Server stack must own the implementation themselves
    or write their own `IStrataraAuthenticationStateProvider` implementation.
  - `AddBlazorIdentity<TUser, TIdentityDbContext>()` DI extension. Replaced by
    `AddAspNetIdentityWithSignInManager<TUser, TIdentityDbContext>()` (same
    `SignInManager` wrapper plus localisation wiring, but **no**
    `AuthenticationStateProvider` forwarder registration).
  - `LoggerIdentityExtensions.LogAuthStateChangeProcessingFailed` (was used
    exclusively by the deleted `BlazorAuthenticationStateProvider`).

### Changed (BREAKING)

- **Rename**: `BlazorSignInManager<TUser>` → `AspNetSignInManager<TUser>`. The
  class is a generic `SignInManager` wrapper with no Blazor code in it — the
  old name was misleading. Consumer implementation code (typically only DI
  registration via the extensions) remains source-compatible through the
  `IStrataraSignInManager` interface; only direct up-casts to the concrete
  class name break.
- **`IdentityNoOpEmailSender`**: body is now directly `Task.CompletedTask`
  instead of forwarding to `Microsoft.AspNetCore.Identity.UI.Services.NoOpEmailSender`.
  Behaviour identical (both send no mail); benefit: no
  `Microsoft.AspNetCore.Identity.UI` transitive needed.
- **`Stratara.Identity.AspNetCore.csproj`**:
  `Microsoft.AspNetCore.Components.WebAssembly.Server` `PackageReference`
  removed; replaced by `<FrameworkReference Include="Microsoft.AspNetCore.App" />`.
  Cleanup side effect: `Microsoft.Extensions.Identity.Stores`,
  `Microsoft.Extensions.Localization`, and
  `Microsoft.Extensions.Hosting.Abstractions` (prunable through the framework
  reference) also removed (NU1510). PackageId stays
  `Stratara.Identity.AspNetCore`; package description/tags no longer mention
  Blazor.
- **Namespace alignment**: every namespace in the seven affected packable
  packages now leads with its own assembly name. Cross-cutting breaking change:
  every consumer must update `using` statements. Mappings:
  - `Stratara.Shared.Abstractions.*` → `Stratara.Abstractions.*`
  - `Stratara.Domain.Abstractions` → `Stratara.Abstractions.Domain`
  - `Stratara.Shared.{Authorization, BackgroundTasks, EventSourcing, Outbox, Security, Merging.ChangeTracking}` → `Stratara.Abstractions.{...}`
  - `Stratara.Shared.Resilience` → `Stratara.Resilience`
  - `Stratara.Shared.Diagnostics` → `Stratara.Diagnostics` (logger extensions in
    `Stratara.Shared.Diagnostics.Extensions` deliberately retained as a cross-
    project convention)
  - `Stratara.Shared.Multitenancy` → `Stratara.Sessions.Multitenancy`
  - `Stratara.Infrastructure.{Mediator, Authorization}` (mediator project side)
    → `Stratara.Mediator.{*, Authorization}`
  - `Stratara.Infrastructure.{Outbox, Messaging, Mediator, Projections}`
    (Outbox.RabbitMQ project side) →
    `Stratara.Outbox.RabbitMQ.{Outbox, Messaging, Mediator, Projections}`
  - `Stratara.Infrastructure.{Session, Middlewares}` (SessionContext project
    side) → `Stratara.Sessions.{Session, Middlewares}`
  - `Stratara.{WriteStore.*, ReadStore.*, IdentityStore, EntityFrameworkCore.*}`
    → `Stratara.EventSourcing.EntityFrameworkCore.{...}`
- **Package rename**: `Stratara.SessionContext` → `Stratara.Sessions`. The
  previous package name collided after the namespace alignment with the type
  name `Stratara.Contracts.Session.SessionContext` (wire record) — inside files
  that have `Stratara.SessionContext` as a dependency, the namespace shadowed
  the type and triggered `CS0118: 'SessionContext' is a namespace but is used
  like a type`. The new name `Stratara.Sessions` is collision-free and
  describes the package semantically better (session runtime: provider plus
  middleware), independent of the record itself, which continues to live in
  `Stratara.Contracts.Session`.
- **Consumer impact**: every consumer must
  - reassign `using` statements (cross-cutting, mechanical — sed/IDE refactor)
  - `<PackageReference Include="Stratara.SessionContext" />` →
    `<PackageReference Include="Stratara.Sessions" />`
- Previous `Stratara.SessionContext` packages remain available on the internal
  Azure Artifacts feed; the new package `Stratara.Sessions` replaces them
  starting from v2.0.0. Side-by-side install is not supported (same types).

## [1.5.1] — 2026-05-22

### Fixed

- **`Stratara.Outbox.RabbitMQ`** (`RabbitMqBus.SubscribeAsync`): client
  subscriptions (subscription name with prefix `default-`) now declare their
  queue as `exclusive=true` in addition to `autoDelete=true`. Previously:
  `durable=false, exclusive=false, autoDelete=true` — that combination is
  **rejected by RabbitMQ 4.x with default config**
  (`INTERNAL_ERROR — Feature 'transient_nonexcl_queues' is deprecated`). The
  new `exclusive=true` declaration expresses the connection-bound lifetime
  explicitly and is accepted by all RabbitMQ versions. Worker subscriptions
  (durable + non-exclusive) are unchanged.

### Added

- **Build system**: new `build/PackageReleaseNotes.targets`, included via
  `Directory.Build.props`. A `RoslynCodeTaskFactory`-based MSBuild task
  extracts the `## [<VersionPrefix>]` section from `CHANGELOG.md` at `dotnet
  pack` time and sets it as `<PackageReleaseNotes>`. The CHANGELOG is now the
  single source for release notes; local `pack` and the CI pack step produce
  identical `.nuspec` `<releaseNotes>` content. Active only when
  `IsPackable=true`; when the section can't be found, the field is left empty
  (no build error).
- **`Stratara.Outbox.RabbitMQ` tests**: 22 new unit tests in the existing
  `tests/Stratara.Outbox.RabbitMQ.Tests` project:
  - `CommandOutboxDispatcherTests` (7) — direct publish success, replay-active
    falls back to outbox write, bus failure falls back to outbox write, null
    `SessionContext` throws, `EnqueueOutboxEntriesAsync` replay short-circuit,
    success delete, failure keep.
  - `EventBundleOutboxDispatcherTests` (6) — analogous coverage for event
    bundles.
  - `MediatorCommandWorkerTests` (5) — `DispatchAsync` for plain commands plus
    `IAggregateScopedCommand` bucket-lock path; unknown type / invalid command
    JSON / invalid `SessionContext` JSON throw `InvalidOperationException`;
    `SessionContext` is correctly restored.
  - `OutboxWorkerTests` (4) — lock-not-acquired skips drain; empty batches skip
    the dispatcher; the batch loop terminates on an empty follow-up batch;
    `IOutboxLockHandle` is disposed after the drain. Drives the
    `BackgroundService` via `StartAsync`/`StopAsync` and synchronises via
    `TaskCompletionSource` on the first `TryAcquireAsync` call.
  - `MessagingServiceCollectionExtensionsTests` (4) — `AddMessaging`
    registers `RabbitMqBus` as an `IMessageBus` singleton, `MessagingIdentifier`
    as an `IMessagingIdentifier` singleton, binds `MessagingOptions` from the
    `Messaging` section, and returns the builder for chaining.
  - `Stratara.Outbox.RabbitMQ.csproj`: `InternalsVisibleTo` for
    `Stratara.Outbox.RabbitMQ.Tests` added so the test can access the internal
    `MediatorCommandWorker.DispatchAsync`.
  - `ResiliencePipelineProvider<string>` mock returns `ResiliencePipeline.Empty`
    — no real retry delays in the unit tests; the semantic behaviour is
    orthogonal to the concrete Polly pipeline and is covered separately by the
    integration test suite.
- **`Stratara.Sagas` tests**: new test project `tests/Stratara.Sagas.Tests/`
  (registered in `Stratara.slnx` and the publish solution filter) with 23 unit
  tests:
  - `SagaMethodInvokerTests` (9) — reflection cache returns the declared event
    types (public and private `HandleAsync` methods), unpacks `IEvent<TEvent>`
    correctly to `TEvent`, empty result for sagas without handlers, reference-
    cache identity on repeated calls. `GetOrCreateDelegate` returns a compiled
    delegate (invocation against a real saga is verified), `NoOp` for unknown
    event types, same delegate instance on repeated calls. `IsNoOp`
    differentiates NoOp from a real delegate.
  - `SagaHandlerTests` (6) — direct-handler path used when `IsNoOp == false`;
    wrapped path (`IEvent<TEvent>` overload) used when the direct path is NoOp;
    multiple events dispatched in order; `GetSagaName` / `GetRelevantEventTypes`
    / `GetRelevantEventTypeNames` delegate correctly to the invoker and map
    types to qualified names.
  - `SagaManagerTests` (4) — fan-out to all registered sagas in parallel;
    per-saga filter through the `relevantTypeNames` subset; sagas without
    relevant events are NOT dispatched (short-circuit plus log); empty saga
    collection does not throw.
  - `SagaServiceCollectionExtensionsTests` (4) — `AddSagaWorker` registers
    `ISagaManager` / `ISagaHandler` / `ISagaMethodInvoker` each as `Scoped`
    plus `SagaWorker` as a singleton `HostedService`.
    `AddSagasFromAssemblyContaining<T>` finds concrete `ISaga`
    implementations and registers them as `Scoped`; abstract classes and
    interfaces are skipped.
- **`Stratara.Identity.Core` plus `Stratara.Identity.AspNetCore` tests**: two
  new test projects:
  - `tests/Stratara.Identity.Core.Tests/` (2 tests) — `HttpClientHelperTests`:
    the helper returns the injected `HttpClient`; implements `IHttpClientHelper`.
    Models are all `[ExcludeFromCodeCoverage]` and need no tests — hence the
    small project.
  - `tests/Stratara.Identity.AspNetCore.Tests/` (14 tests):
    - `IdentityNoOpEmailSenderTests` (3) — all three send methods
      (`SendConfirmationLinkAsync`, `SendPasswordResetLinkAsync`,
      `SendPasswordResetCodeAsync`) do not throw.
    - `AddDevelopmentNoOpEmailSenderTests` (3) — registers `IEmailSender<TUser>`
      as `Scoped` in Development; throws `InvalidOperationException` in
      Production; runs cleanly in Staging (production-guard specification).
    - `IdentityResourcesLocalizationTests` (4) — i18n verification:
      `IStringLocalizer<IdentityResources>` returns the English default for the
      `en` culture, German overrides for the `de` culture, falls back to
      English for unsupported cultures (`fr`), and the English and German
      texts are actually different. Culture switching via
      `CultureInfo.CurrentUICulture` with try/finally restore through a
      `CultureScope` disposable.
    - `AddBlazorIdentityTests` (4) — `IStrataraSignInManager` →
      `BlazorSignInManager<TUser>` `Scoped`,
      `IStrataraAuthenticationStateProvider` →
      `BlazorAuthenticationStateProvider<TUser>` `Scoped`
      (implementation-type-name match because `internal sealed`),
      `AuthenticationStateProvider` is registered (forwarder),
      `IStringLocalizerFactory` is registered (so
      `IStringLocalizer<IdentityResources>` remains resolvable without further
      configuration).
- **`Stratara.EventSourcing.Pipeline.CommandAudit` plus
  `Stratara.EventSourcing.WorkerDefaults` tests** (both previously 0%
  coverage): two new test projects:
  - `tests/Stratara.EventSourcing.Pipeline.CommandAudit.Tests/` (5 tests) —
    `CommandAuditBehavior<TRequest>` (no result) plus
    `CommandAuditBehavior<TRequest, TResult>` (with result): command request
    writes the audit row and calls `next()`; the audit-write-before-handler
    order is verified (callback sequence); non-command requests (`IQuery<T>`
    or naked `IRequest`) skip the audit write and call `next()` directly;
    result is passed through. Mocks: `IWriteUnitOfWork`, `ITransaction`,
    `ICommandAuditRepository`.
  - `tests/Stratara.EventSourcing.WorkerDefaults.Tests/` (8 tests) — smoke
    coverage for all six composite DI extensions: `AddBackendServices`,
    `AddCommandWorkerServices`, `AddEventProjectionWorkerServices`,
    `AddSagaWorkerServices`, `AddEventStreamHashWorkerServices`,
    `AddOutboxWorkerServices`. Each test asserts that, after the composite
    call, the characteristic `HostedService` (e.g. `MediatorCommandWorker`,
    `SagaWorker`, `EventStreamHashWorker`, `OutboxWorker`) plus the core
    services (`IMediator`, `ICommandOutboxDispatcher`,
    `IProjectionReplayState`, etc.) are registered. Additionally: the builder
    is returned for chaining; `AddCommonFrameworkServices` runs as a private
    composite implicitly in EVERY one of the six — verified via
    `ISessionContextProvider` registration (comes exclusively from the
    private path). Guards against refactor regressions in which a single
    composite accidentally loses `AddCommonFrameworkServices`.
- **`Stratara.ServiceDefaults` plus `Stratara.ServiceDefaults.AspNetCore`
  tests** (both previously 0% coverage): two new test projects:
  - `tests/Stratara.ServiceDefaults.Tests/` (9 tests):
    - `OpenTelemetryExtensionsTests` (5) — `ConfigureOpenTelemetry` registers
      `TracerProvider` plus `MeterProvider`; builder chaining; optional
      `configureMetrics` / `configureTracing` callbacks invoked; without
      `OTEL_EXPORTER_OTLP_ENDPOINT`, no OTLP exporter is registered
      (conditional branch of `AddOpenTelemetryExporters`).
    - `SerilogExtensionsTests` (4) — `ConfigureSerilog` registers
      `Serilog.ILogger` as a service plus at least one `ILoggerProvider`;
      builder chaining; `ConfigureSerilogBootstrapLogger` replaces `Log.Logger`
      globally (restore via try/finally).
  - `tests/Stratara.ServiceDefaults.AspNetCore.Tests/` (5 tests):
    - `HealthCheckExtensionsTests` (3) — `AddDefaultHealthChecks` registers
      exactly one `self` check with tag `live` (via
      `IOptions<HealthCheckServiceOptions>.Registrations`);
      `HealthCheckService` is in the service container; builder chaining.
    - `OpenTelemetryExtensionsTests` (2) — `ConfigureAspNetOpenTelemetry`
      chains through `ConfigureOpenTelemetry` and registers `TracerProvider`
      plus `MeterProvider`; builder chaining.
- **`Stratara.ServiceDefaults.AspNetCore` endpoint-map tests**: five new tests
  in `tests/Stratara.ServiceDefaults.AspNetCore.Tests/MapDefaultEndpointsTests.cs`.
  Boots a `WebApplication` via `WebApplication.CreateBuilder()` plus
  `WebHost.UseTestServer()` and hits the mapped endpoints via the
  `TestServer` `HttpClient`:
  - `/health` returns `200 OK` for anonymous callers (default
    `requireAuthorizationOnHealth=false`).
  - `/alive` returns `200 OK` for anonymous callers (always, regardless of the
    authorization flag — Kubernetes / Aspire liveness probes have no
    credentials).
  - With `requireAuthorizationOnHealth=true`: `/alive` stays 200 OK; `/health`
    does NOT answer with 200 (verifies the authorization gating; the specific
    status depends on the configured auth scheme — the test accepts any
    `!= OK`).
  - `MapDefaultEndpoints` returns the same `WebApplication` instance for
    chaining.
  - Test helper: `NoOpAuthHandler` as a minimal `AuthenticationHandler<>` that
    always returns `AuthenticateResult.NoResult()` — sufficient to resolve the
    authorization layer at all without a real auth scheme.
  - **New in `Directory.Packages.props`:** `Microsoft.AspNetCore.Mvc.Testing`
    10.0.8 added as a central test dependency.
- **`Stratara.Projections` `ProjectionReplayWorker` tests**: 8 new tests in
  `tests/Stratara.Projections.Tests/Services/ProjectionReplayWorkerTests.cs`
  (previously not covered — worker as `BackgroundService`). Drives the worker
  via `StartAsync` plus stop, captures the
  `IProjectionReplayState.SubscribeToReplayRequestAsync` callback with a
  `Func<Task>` capture, and invokes it explicitly at test time:
  - Subscribe registers without trigger → no `Activate`, no truncate.
  - Happy path → `Activate` → `TruncateAllAsync` → `GetMaxSequenceNumberAsync`
    → `ProjectionManager.HandleAsync` → `SetProgress(processed, total)` →
    `Deactivate` in the expected order.
  - Empty stream (`GetMaxSequenceNumberAsync` = 0, empty batch) → truncate
    still runs, `HandleAsync` does NOT, `Deactivate` runs.
  - Multi-batch → `afterSequence` is correctly advanced between batches
    (verified via capture: `[0, 20, 30]` sequence for three entries).
  - Failure path → exception in `TruncateAllAsync` is caught,
    `SetFailed(message)` is called, `Deactivate` runs (via `finally`).
  - Failure message > 500 chars → truncated to 500 chars + `…` (length = 501).
  - `OperationCanceledException` → silent swallow (no `SetFailed`),
    `Deactivate` runs.
  - `SessionContext` restoration → `ISessionContextProvider.Set` is called per
    entry with `TenantId` plus `ActorTenantId` from the `EventStreamEntry`.
  - **New in `tests/Stratara.Projections.Tests.csproj`:** `PackageReference`s
    for `Microsoft.Extensions.DependencyInjection` and
    `Microsoft.Extensions.Hosting` (for `ServiceCollection.BuildServiceProvider()`
    plus `BackgroundService.StartAsync`).
- **`Stratara.EventSourcing.EntityFrameworkCore` ReadStore plus IdentityStore
  tests**: 10 new tests in `tests/Stratara.EntityFrameworkCore.Tests/`. Pins
  the DbContext configuration isolation in which WriteStore-/ReadStore-/
  IdentityStore-`IEntityTypeConfiguration<>` implementations in the same
  assembly are separated by namespace predicate:
  - `ReadStore/ReadDbContextTests.cs` (4 tests):
    `TestReadDbContext : ReadDbContext<TestReadDbContext>` plus EF Core
    InMemory → model registers `TenantView` plus `AttributeDefinition` (from
    the `Stratara.ReadStore.*` namespace filter); does NOT register
    `EventStreamEntry` / `OutboxEntry` / `Snapshot` (write store) or
    `IdentityUser` / `IdentityRole` (identity store); `EnsureCreated` runs
    without throwing.
  - `IdentityStore/IdentityStoreTests.cs` (6 tests):
    `TestIdentityStore : IdentityStore<TestIdentityStore, IdentityUser>` plus
    EF Core InMemory → model registers `IdentityUser` plus `IdentityRole`
    (from `IdentityDbContext<>` base); registers the
    `IdentityUserPasskey<string>` entity with table name `AspNetUserPasskeys`
    and `CredentialId` as primary key (explicit configuration in
    `OnModelCreating`); does NOT register `EventStreamEntry` (write store) or
    `TenantView` (read store); `EnsureCreated` runs without throwing.
  - `Microsoft.AspNetCore.Identity.EntityFrameworkCore` is pulled in
    transitively through the source project reference — no additional
    `PackageReference` in the test csproj needed.
- **`Stratara.Outbox.RabbitMQ` `RabbitMqBus` integration tests**: 5 new tests
  in `tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Messaging/RabbitMqBusTests.cs`
  against Testcontainers RabbitMQ (`rabbitmq:4-management-alpine`):
  - Publish → subscribe roundtrip (default client queue, one TestMessage).
  - Handler success → message is ACK'd (counter stays at 1 after processing).
  - Handler throws `ConcurrencyException` → message is NACK'd with
    `requeue=true`; re-delivery is visible (counter ≥ 2 after the 2nd attempt).
  - Handler throws generic exception → message is NACK'd with `requeue=false`;
    NOT re-delivered (counter stays 1, "poison message" path).
  - Worker subscription (durable) persists message between disconnect plus
    re-subscribe.
  - **Bug fix as a consequence**: see `### Fixed` above. Test #1 surfaced the
    `transient_nonexcl_queues` problem.
  - **New in `Directory.Packages.props`**: `Testcontainers.RabbitMq` 4.12.0
    plus `Microsoft.Extensions.Configuration` 10.0.8 (for `ConfigurationBuilder`
    in the fixture).
  - **New fixture**: `RabbitMqFixture` (xUnit collection fixture, parallel to
    `RedisFixture`) boots a RabbitMQ container per collection and exposes an
    `IConfiguration` with `ConnectionStrings:rabbitmq` → URI constructor path
    of `RabbitMqBus`.

## [1.5.0] — 2026-05-22

### Changed

- **`Stratara.Identity.AspNetCore`** (`BlazorSignInManager`): the five
  user-facing failure messages (`Identity.SignIn.Lockout`, `…NotAllowed`,
  `…InvalidCredentials`, `…InvalidTwoFactor`, `…InvalidRecoveryCode`) are no
  longer hardcoded German; they are resolved through
  `IStringLocalizer<IdentityResources>`. English is the default resource;
  German (`de`) ships as a satellite assembly. Selection follows
  `CultureInfo.CurrentUICulture`. **Consumer impact:** existing login UIs now
  see English default messages unless `CurrentUICulture` is explicitly set to
  `de`. Solution: `app.UseRequestLocalization(new RequestLocalizationOptions
  { DefaultRequestCulture = new("de"), SupportedUICultures = new[] { new
  CultureInfo("de"), new CultureInfo("en") } })` (or keep `de` as the global
  default for the app).

### Added

- **`Stratara.Identity.AspNetCore`**: new `Resources/IdentityResources` anchor
  class plus `IdentityResources.resx` (en) plus `IdentityResources.de.resx`
  (de). `AddBlazorIdentity` calls `services.AddLocalization()` so that
  `IStringLocalizer<IdentityResources>` is resolvable without further
  configuration.
- **`Directory.Packages.props`**: `Microsoft.Extensions.Localization` 10.0.8
  as a centrally-managed dependency.

## [1.4.0] — 2026-05-21

### Changed

- **`Stratara.Identity.Core`** (interfaces): all async methods on
  `IStrataraSignInManager` (5 methods), `IStrataraAuthenticationStateProvider`
  (5 methods), and `ITokenStorage` (2 methods) gain an additive
  `CancellationToken cancellationToken = default` parameter as the last
  argument. **Source-compatible** for existing callers (the default parameter
  is assumed); **implementor-breaking** — anyone who implements the interface
  must adjust their method signatures.
- **`Stratara.Identity.AspNetCore`** (`BlazorSignInManager`,
  `BlazorAuthenticationStateProvider`): Stratara's own implementations forward
  the token as far as the underlying ASP.NET Core `SignInManager` API accepts
  it (it currently doesn't — the methods call `ThrowIfCancellationRequested`
  before the delegate call so that an already-cancelled operation doesn't load
  the database).

## [1.3.0] — 2026-05-21

### Changed

- **`Stratara.Identity.Core`** (`StrataraSignInResult`): no longer inherits
  from `Microsoft.AspNetCore.Identity.SignInResult`. The class already had all
  the required properties (`Succeeded`, `IsLockedOut`, `RequiresTwoFactor`,
  `LoginFailureMessage`, `AccessTokenInfo`, `UserId`) — the inheritance was
  only historical. With this change, `Stratara.Identity.Core` is now fully
  channel-agnostic and consumable from MAUI / console / pure-unit-test
  contexts without a transitive ASP.NET Core dependency. The `new bool
  RequiresTwoFactor` (which previously hid the base property) has become a
  normal property. **Technically breaking** for code that up-casts
  `StrataraSignInResult` to `SignInResult` or reads the inherited
  `IsNotAllowed` property (which was never set by Stratara — always `false`).

### Removed

- **`Stratara.Identity.Core`**: `Microsoft.Extensions.Identity.Core`
  `PackageReference` removed (it was only needed for the `SignInResult` base,
  which is now gone).

## [1.2.0] — 2026-05-21

### Added

- **`Stratara.Abstractions`**: new `IOutboxLock` plus `IOutboxLockHandle`
  abstraction pair in namespace `Stratara.Shared.Abstractions.Outbox`.
  Coordinates concurrent outbox-drain attempts between multiple worker
  instances.
- **`Stratara.Outbox.RabbitMQ`**: two `IOutboxLock` implementations —
  `NullOutboxLock` (no-op, default, preserves the historical single-instance
  assumption) and `RedisOutboxLock` (distributed lock via `SET NX EX` on key
  `stratara:outbox:lock`, release via value-checked Lua script). Opt-in via a
  new `AddRedisOutboxLock()` DI helper. Requires an already-registered
  `IConnectionMultiplexer` (e.g. via `AddCaching()`).
- **`Stratara.Outbox.RabbitMQ`** (`OutboxOptions`): new `LockLeaseSeconds`
  property (default 60 s). Controls how long the worker leases the lock per
  drain cycle.

### Changed

- **`Stratara.Outbox.RabbitMQ`** (`OutboxWorker`): each polling cycle is now
  guarded by `IOutboxLock.TryAcquireAsync`. If the lock is not granted, the
  worker skips the cycle and tries again at the next polling interval.
  Constructor extended with the additional `IOutboxLock` parameter. With the
  default `NullOutboxLock`, runtime behaviour is unchanged; with
  `RedisOutboxLock`, multi-replica deployments without duplicate-publishing
  risk are possible for the first time.

### Diagnostics

- New `LogEvents.OutboxProcessing` event IDs: `OutboxLockNotAcquired = 106_005`
  (Debug, "cycle skipped, lock is held by someone else"), `OutboxLockUnavailable
  = 106_106` (Warning, Redis outage), `OutboxLockReleaseFailed = 106_107`
  (Warning, release failed — lease will expire automatically).

## [1.1.2] — 2026-05-21

### Changed

- **`Stratara.Outbox.RabbitMQ`** (Service Bus path): `ServiceBus.SubscribeAsync`
  now classifies handler exceptions explicitly. Success →
  `CompleteMessageAsync`, `ConcurrencyException` → `AbandonMessageAsync`
  (Service Bus redelivers), all other exceptions → `DeadLetterMessageAsync`
  with the exception type as reason. Previously every success/failure ended in
  `CompleteMessageAsync` unconditionally, so errors were silently lost or only
  ended up in the DLQ automatically after the max-delivery count. Mirrors the
  RabbitMQ NACK logic.

### Security

- **`Stratara.ServiceDefaults`** plus **`Stratara.ServiceDefaults.AspNetCore`**:
  HTTP header redaction on the OpenTelemetry HttpClient and ASP.NET Core
  instrumentation. `EnrichWith*` callbacks replace
  `http.request.header.authorization`, `http.request.header.cookie`,
  `http.request.header.proxy_authorization`, and
  `http.response.header.set_cookie` with `"REDACTED"`. OpenTelemetry doesn't
  capture headers by default; the defensive layer kicks in only when a
  consumer explicitly opts in to header capture (e.g. via
  `OTEL_INSTRUMENTATION_HTTP_CAPTURE_HEADERS`). Prevents bearer tokens and
  session cookies from landing in OpenTelemetry exporter traces.

### Documentation

- **`Stratara.Outbox.RabbitMQ`**: `<remarks>` on the at-least-once / DLQ
  semantics is now consistent across all components (`CommandOutboxDispatcher`,
  `EventBundleOutboxDispatcher`, `ServiceBus`, `RabbitMqBus`,
  `MediatorCommandWorker`).
- **`Stratara.Sagas`**: `SagaMethodInvoker` class doc extended with a
  `<remarks>` block on cache lifecycle (process lifetime, plateauing) and
  unbounded-growth risk (plugin systems / hot-reload / dynamic saga
  assemblies). Mitigation via custom `ISagaMethodInvoker` with a bounded
  cache (LRU or size-limited) documented. Inline comment migrated into the
  XML doc block.

## [1.1.1] — 2026-05-21

### Added

- **`Stratara.ServiceDefaults.AspNetCore`**: optional parameter
  `requireAuthorizationOnHealth` on `MapDefaultEndpoints`. Default `false` (no
  breaking change); when set to `true`, `RequireAuthorization()` is applied to
  the `/health` endpoint. The `/alive` endpoint remains anonymous for
  Kubernetes / Aspire liveness probes. A `<remarks>` block documents the
  information-disclosure risk of the default mapping and three mitigation
  options.
- **`Stratara.Contracts`**: explicit `[JsonPropertyName]` attributes on all
  wire records (`CommandEnvelope`, `EventBundle`, `EventMessage`,
  `PagedRequest`, `SessionContext`). Pins the wire format to PascalCase — no
  behaviour change for existing consumers, but independent of consumer-side
  `JsonSerializerOptions.PropertyNamingPolicy` overrides.

### Changed

- **`Stratara.ServiceDefaults`**: OTLP exporter timeout defaulted to 5000 ms
  (instead of the OTel spec default of 10 000 ms). Keeps host shutdown and
  metric flushes responsive when the collector is unreachable. Consumers can
  override via `OTEL_EXPORTER_OTLP_TIMEOUT`.
- **`Stratara.Identity.AspNetCore`**: internal DRY refactor of the
  password-policy / lockout / passkey defaults into private static helpers
  (`AddAspNetIdentity` and `AddBlazorIdentity` now share the configuration).
  No API change.

### Security

- **`Stratara.Identity.Core`**: explicit `<remarks>` warnings on
  `LoginResponse.AccessToken` and `LoginResponse.RefreshToken` (and on
  `LoginResponse`, `AccessTokenInfo`, `StrataraSignInResult.AccessTokenInfo`,
  and `ITokenStorage`). Calls out that refresh tokens are strictly more
  sensitive than access tokens and must only be persisted in encrypted /
  platform-secure storage.

### Fixed

- **Build system**: `Directory.Build.props:62` — the condition
  `Condition="'$(IsPackable)' == 'true'"` on `<GenerateDocumentationFile>`
  never matched, because props evaluate before csproj contents. CS1591 was
  effectively disabled. `GenerateDocumentationFile` is now unconditionally
  active; test and sample projects suppress CS1591 via `NoWarn`. While
  sharpening this, 10 hidden XML-doc gaps were uncovered and fixed
  (`IUserIdentity.UserId`, `IPipelineBehavior` / `IWriteUnitOfWork` /
  `ISecureBlobEncryptor` class-level `<paramref>` → `<c>`,
  `CommandEnvelopeMapper` ambiguous `Guid.CreateVersion7` cref,
  `TypeExtensions` unresolved name cref, `LoggerSagaExtensions` class summary,
  `IProjectionsUnitOfWork`, `ProjectionWorker`, `IdentityNoOpEmailSender`,
  `MediatorCommandWorker`).

### Documentation

- **Full XML doc coverage** across all eight packable surfaces —
  Microsoft style, with `<summary>` on every public member plus `<remarks>`
  for semantic notes (idempotency, at-least-once delivery, AES-GCM AAD
  contracts, DbContext isolation, single-instance worker assumptions).
  Affected packages: `Stratara.Outbox.RabbitMQ`, `Stratara.Shared`,
  `Stratara.Infrastructure`, `Stratara.Projections`,
  `Stratara.EventSourcing.EntityFrameworkCore` (in addition to the previously
  documented `Stratara.Domain`, `Stratara.Sagas`, `Stratara.Contracts`).
- **Sagas README** extended with lifecycle (scoped-per-bundle, no durable
  state, at-least-once), correlation (event-type routing, session-context
  restoration, app-side aggregate id), state management (drive via
  `ICommandOutboxDispatcher`, not direct mutation), and
  `[JetBrains.Annotations.UsedImplicitly]` guidance for handlers and saga
  classes.

## [1.1.0] — 2026-05-20

### Changed

- **Aspire wrapper removed**: `AddCaching` and the thin Aspire wrapper were
  trimmed. Consumers moved to 1.1.0 to pick up the leaner behaviour.

### Background

- First stable release of the lockstep'd Stratara family after the
  restructuring phase. All 20 packable packages share this version.

## Earlier versions

Earlier `0.x` and `1.0.x` preview versions (during the restructuring phase)
remain findable on the internal Azure Artifacts feed but are not documented
retroactively here.

[Unreleased]: https://github.com/yesbert/Stratara/compare/v3.4.0...main
[3.4.0]: https://github.com/yesbert/Stratara/releases/tag/v3.4.0
[3.3.0]: https://github.com/yesbert/Stratara/releases/tag/v3.3.0
[3.2.3]: https://github.com/yesbert/Stratara/releases/tag/v3.2.3
[3.2.2]: https://github.com/yesbert/Stratara/releases/tag/v3.2.2
[3.2.1]: https://github.com/yesbert/Stratara/releases/tag/v3.2.1
[3.2.0]: https://github.com/yesbert/Stratara/releases/tag/v3.2.0
[3.1.7]: https://github.com/yesbert/Stratara/releases/tag/v3.1.7
[3.1.6]: https://github.com/yesbert/Stratara/releases/tag/v3.1.6
[3.1.5]: https://github.com/yesbert/Stratara/releases/tag/v3.1.5
[3.1.4]: https://github.com/yesbert/Stratara/releases/tag/v3.1.4
[3.1.3]: https://github.com/yesbert/Stratara/releases/tag/v3.1.3
[3.1.2]: https://github.com/yesbert/Stratara/releases/tag/v3.1.2
[3.1.1]: https://github.com/yesbert/Stratara/releases/tag/v3.1.1
[3.1.0]: https://github.com/yesbert/Stratara/releases/tag/v3.1.0
[3.0.23]: https://github.com/yesbert/Stratara/releases/tag/v3.0.23
[3.0.22]: https://github.com/yesbert/Stratara/releases/tag/v3.0.22
[3.0.21]: https://github.com/yesbert/Stratara/releases/tag/v3.0.21
[3.0.20]: https://github.com/yesbert/Stratara/releases/tag/v3.0.20

<!--
  Older release entries (3.0.0 through 3.0.19, plus the 1.x and 2.x lines) were
  cut and tagged on the internal Azure DevOps repository only — v3.0.20 was the
  first version published to the public GitHub mirror. The section headings still
  function as anchors (e.g. #3019--2026-05-27) within this file.
-->

