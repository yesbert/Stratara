> **Status:** approved

# Retire the deprecated members that were held for the major

## Why

Six members of the published surface carry `[Obsolete]`, each naming its successor and each saying —
in its own message or in the README beside it — that it goes in the next major version. That version
is `4.0.0`, and it is now being assembled.

A deprecation is a promise with two halves. The first half was kept: every one of these has shipped
with a successor, a migration sentence and at least one minor version of overlap. **The second half
is this change.** A deprecation that is never collected stops being a migration aid and becomes part
of the surface — consumers write against it, the removal gets harder every release, and the warning
that was supposed to be temporary is just noise a consumer learns to suppress.

There is a second, smaller reason, and it is the one with a date on it. `S1133` — *"do not forget to
remove this deprecated code someday"* — fires on exactly these six members, and they are the only
findings the nightly analysis will still report once `leave-no-analysis-issue-open` ships. The
project's answer is zero open issues. Six reminders that exist to be collected are collected here,
which is also the only honest way to close them: suppressing them would silence the reminder while
leaving the thing it reminds about.

### What is being removed, and what replaces it

Every one has a named successor, so each migration is a mechanical substitution at the call site.

**1. The two type-less snapshot lookups** — `ISnapshotRepository` and its Entity Framework Core
implementation:

```csharp
// before                                        // after
snapshots.GetAsync(streamId, toVersion, ct);     snapshots.GetAsync(streamId, aggregateTypeName, toVersion, ct);
snapshots.GetLatestVersionOrDefaultAsync(         snapshots.GetLatestVersionOrDefaultAsync(
    streamId, ct);                                    streamId, aggregateTypeName, ct);
```

A lookup that does not name the aggregate type can return a snapshot written for a *different* type
that shares the stream id, which is then deserialized into the requested type and yields corrupt or
default state. `aggregate-rehydration` already requires the opposite — *"A snapshot SHALL be selected
by both the stream and the aggregate type being rebuilt"* — so these two overloads are a hole in the
surface that the specification already closed. The framework itself stopped calling them when the
type-scoped overloads landed: `AggregationService` and `SnapshotService` both pass the qualified type
name today.

The `aggregateTypeName` a caller needs is the same value the framework uses,
`aggregateType.GetQualifiedTypeName()`. It is not new information the consumer must invent.

**2. The misspelled registration alias** — `AddNpsqlWriteDbContextFactory` (no `g`):

```csharp
// before                                        // after
services.AddNpsqlWriteDbContextFactory<T>();     services.AddNpgsqlWriteDbContextFactory<T>();
```

It forwards verbatim to the correctly-spelled name and has since `3.2.0`. The README of
`Stratara.EventSourcing.EntityFrameworkCore` states outright that the old spelling *"will be removed
in the next major version"*.

**3. The bare-403 middleware registration** — `UseAuthorizationExceptionTo403()`:

```csharp
// before                                        // after
app.UseAuthorizationExceptionTo403();            builder.Services.AddStrataraProblemDetails();
                                                 app.UseExceptionHandler();
```

It maps an authorization refusal and a tenant-access denial to a status code with no body.
`AddStrataraProblemDetails()` in `Stratara.ServiceDefaults.AspNetCore` maps the same two refusals —
plus validation failures — to one RFC 7807 problem shape, and the two must never be registered
together, because the middleware answers first and the handler never sees the exception. Removing the
older one removes that trap along with it.

## What Changes

The consumer-visible effect: six members disappear from the published surface. A consumer still
calling one stops compiling, at the call site, with a successor named in the deprecation message it
has been seeing since the member was deprecated. **BREAKING**, deliberately, and in the version where
a break belongs.

- **BREAKING** — `ISnapshotRepository.GetAsync(Guid, long?, CancellationToken)` and
  `ISnapshotRepository.GetLatestVersionOrDefaultAsync(Guid, CancellationToken)` are removed from the
  interface and from `SnapshotRepository`. A consumer that implements `ISnapshotRepository` itself
  also stops having to implement them, which is a break in the helpful direction.
- **BREAKING** — `AddNpsqlWriteDbContextFactory<TDbContext>()` is removed.
- **BREAKING** — `UseAuthorizationExceptionTo403()` is removed, and with it the internal
  `AuthorizationExceptionMiddleware` it was the only way to register.
- The `authorization` requirement covering the 403 boundary is amended to describe the one component
  that remains and the shape it answers in. It currently describes a boundary component in terms a
  bare status code satisfies, which is how two components came to satisfy it at once.
- The derived documentation that names a removed member is corrected rather than deleted: three
  guide passages and two package READMEs point at a member that will not exist.

**Nothing else changes.** No package, tier, dependency or wire format moves. No behaviour of any
surviving member changes — a call that compiles after this change does exactly what it did before.
The snapshot table, its schema and its contents are untouched.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `authorization`: the requirement describing how an authorization denial reaches an HTTP caller as
  403 states that the mapping is the opt-in problem-response registration and answers in that shape,
  rather than describing a boundary component loosely enough for a bare status code to qualify.

## Impact

Removed, with the tests that cover them:

- `src/Stratara.Abstractions/Abstractions/EventSourcing/ISnapshotRepository.cs:52,62` — the two
  type-less overloads and their `<remarks>`.
- `src/Stratara.EventSourcing.EntityFrameworkCore/WriteStore/EventSourcing/SnapshotRepository.cs:50,58`
  — their implementations.
- `src/Stratara.EventSourcing.EntityFrameworkCore/EntityFrameworkCore/DependencyInjection/NpgsqlDbContextServiceCollectionExtensions.cs:49`
  — `AddNpsqlWriteDbContextFactory`.
- `src/Stratara.Infrastructure/DependencyInjection/AuthorizationExceptionApplicationBuilderExtensions.cs`
  — the whole file; `UseAuthorizationExceptionTo403` is its only member.
- `src/Stratara.Infrastructure/Middlewares/AuthorizationExceptionMiddleware.cs` — `internal sealed`,
  and the extension above was the only registration reaching it. It is unreachable once that goes.
- `tests/Stratara.Infrastructure.Tests/DependencyInjection/AuthorizationExceptionApplicationBuilderExtensionsTests.cs`
  and `tests/Stratara.Infrastructure.Tests/Middlewares/AuthorizationExceptionMiddlewareTests.cs` —
  they cover only removed code. The behaviour they assert is covered for the surviving path by the
  problem-details tests.

Corrected, because they name a member that will not exist:

- `docs/guides/write-a-validator.md:116` — the paragraph introducing `AddStrataraProblemDetails()` as
  the successor and warning against double registration.
- `docs/guides/enforce-tenant-isolation.md:99` — names `AuthorizationExceptionMiddleware` as what
  maps `TenantAccessDeniedException` to 403.
- `src/Stratara.Mediator/README.md:75` — the same, in the tenant-isolation section.
- `src/Stratara.EventSourcing.EntityFrameworkCore/README.md:54-56` — the rename note, which promises
  the removal this change performs.
- `src/Stratara.ServiceDefaults.AspNetCore/StrataraProblemDetailsServiceCollectionExtensions.cs:25,35`
  — XML documentation warning against registering the obsolete middleware alongside it.

Two more the plan did not name, both found by a gate rather than by reading:

- `tests/Stratara.Documentation.Tests/registration-coverage-allowlist.txt` — exempted both removed
  registrations from the DI-cheatsheet coverage requirement. Its own header says an entry that no
  longer resolves to a registration fails `DiCheatsheetCoverageTests`, so both lines go with their
  subjects. The file stays, header and all: the test reads it, and the next deprecation will need it.
- `scripts/check-doc-symbols.py` — the fabricated-API gate fails on any `Add*`/`Map*`/`Use*` symbol a
  doc names that `src/` does not declare, which is exactly what a migration note does. The gate's own
  reason is that *"readers copy these verbatim"*, and a note reading "removed in 4.0.0, call X
  instead" is the opposite case. A `RETIRED` set alongside the existing `EXTERNAL` and `DOC_LOCAL`
  ones carries the two names with the version that removed them. Without it the only way to pass the
  gate is to stop naming the removed member, which would delete the migration path exactly when a
  consumer needs it.

Unaffected, and worth stating:

- `openspec/specs/aggregate-rehydration/spec.md` — already requires the type-scoped lookup. Removing
  the type-less overloads brings the surface into line with it rather than changing it.
- `openspec/specs/host-composition/spec.md` — the problem-response requirement describes the
  surviving component and needs no amendment.
- `tests/Stratara.Documentation.Tests/RegistrationDocumentationTests.cs:39` — the rule exempting an
  obsolete registration from the worked-example requirement stays. It guards the next deprecation;
  it is not scaffolding for these six.
- Closes the six `S1133` findings deliberately carried out of `leave-no-analysis-issue-open`, which
  records them and says why they were held. This change is where they are collected.
