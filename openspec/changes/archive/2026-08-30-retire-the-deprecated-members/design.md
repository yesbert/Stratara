# Design — Retire the deprecated members that were held for the major

## Context

See [`proposal.md`](proposal.md) — *Why*. What matters here is the state the removals start from,
which was established by reading every call site rather than assumed:

- **Nothing in the framework calls the type-less snapshot overloads.** `AggregationService` passes
  `aggregateType.GetQualifiedTypeName()` and `SnapshotService` passes `aggregateTypeName`; both moved
  to the type-scoped overloads when those landed. The framework's own tests mock the type-scoped
  signatures. So the removal touches no internal caller — only the surface.
- **`AuthorizationExceptionMiddleware` is `internal sealed`.** No consumer can reach it with
  `UseMiddleware<T>()`, and `UseAuthorizationExceptionTo403` is the only place in the repository that
  registers it. Once the extension is gone it is unreachable code, visible only to the test project
  through `InternalsVisibleTo`.
- **`AddNpsqlWriteDbContextFactory` forwards verbatim** to `AddNpgsqlWriteDbContextFactory`. It has no
  body of its own.

The five other deprecated members therefore come out cleanly. The one with a design question attached
is the 403 middleware, and that question is what most of this document is about.

## Goals / Non-Goals

**Goals:**

- Remove all six, so the `S1133` count reaches zero rather than being suppressed to zero.
- Leave the surviving surface behaving exactly as it does today. A call that still compiles after
  this change does the same thing.
- Leave the derived documentation correct, not merely free of dangling names.

**Non-Goals:**

- **No new deprecations.** This change collects a debt; it does not open a new one. Nothing gains an
  `[Obsolete]` here.
- **No change to the problem-details path.** `AddStrataraProblemDetails()` is the successor and it
  ships today, tested. This change does not extend it, retune it or move it.
- **No compatibility shim.** No `[EditorBrowsable(Never)]` survivor, no forwarding overload, no
  `#if`. A major version is where a removal is a removal.
- **Not a review of the remaining surface for further deprecation candidates.** The six are the six
  that carry the attribute.

## Decisions

### Remove the middleware along with its registration, rather than leaving it internal

`AuthorizationExceptionMiddleware` is `internal sealed`, and the extension method being removed is
the only thing that registers it. Keeping it would leave a class no consumer can reach, no framework
code constructs, and only its own unit test exercises — code whose sole remaining purpose would be to
keep its test green.

*Alternative considered: keep the middleware and remove only the extension.* It would preserve the
option of re-exposing a bare-403 path later without rewriting the middleware. Rejected: the middleware
is thirty lines whose logic is a `try`/`catch` around `next(context)`, so "preserving" it saves
nothing worth the confusion of unreachable code in a published package. If a bare-403 path is ever
wanted again, it is a smaller thing to write than to explain.

*Evidence:* `grep` over `src/`, `tests/`, `samples/` and `docs/` — the only references outside the
extension itself are the middleware's own test class and two documentation passages that name it
descriptively. Recorded in the proposal's *Impact* section by exact path.

### Amend the `authorization` requirement rather than leaving it satisfied by accident

The requirement reads *"The framework SHALL offer a boundary component that turns an authorization
denial … into an HTTP 403 response"*. That sentence stays literally true after the removal —
`AddStrataraProblemDetails()` does exactly that. So an amendment is not forced.

It is still the right call, because the loose wording is *how the situation arose*. The requirement
describes a component in terms a bare status code satisfies, which let two components satisfy it at
once — one of them a trap, since registering both means the middleware answers first and the handler
never sees the exception. A requirement that permits two answers to the same question invites a
second implementation. The amended text states the opt-in and the shape, so it describes one path.

*Alternative considered: no delta, `skip_specs: true`.* Defensible on the letter — no surviving
requirement becomes false. Rejected because it would leave the specification unable to tell a reader
which of the two mappings they get, at exactly the moment there stops being a choice. The
specification is the single source; a reader who consults it after this change must not come away
expecting a bare status code.

*Alternative considered: remove the requirement and let `host-composition` carry it alone.*
Rejected: `authorization` is where a consumer looks for how a denial is answered, and
`host-composition` describes the mapping generically across validation, authorization and tenant
isolation. The overlap is deliberate — a capability spans packages, and both capabilities have a
legitimate claim on this behaviour. The two sentences must agree, which after this change they do.

### The removals are one change and one pull request, not six

They share nothing technically — three packages, three unrelated call shapes. What they share is the
promise being kept and the version keeping it. Splitting them would produce six pull requests each
too small to review meaningfully and a CHANGELOG in which the reader has to reassemble what `4.0.0`
broke. One change, one BREAKING entry, one migration table.

*Consequence for the task list:* the tasks are grouped per member so a group can be verified on its
own, and the gauntlet runs once at the end rather than six times.

### The documentation is corrected in the same change, not after it

Five passages name a removed member. Three of them (`write-a-validator.md`,
`StrataraProblemDetailsServiceCollectionExtensions.cs`) exist *because* of the deprecation: they
introduce the successor by contrast with what it supersedes and warn against registering both. Once
there is nothing to supersede, that contrast is a reference to a member a reader cannot find, and the
double-registration warning describes an impossible mistake.

They are rewritten rather than deleted: the successor still needs its worked example, and
`enforce-tenant-isolation.md` and the `Stratara.Mediator` README still need to answer "how does this
exception become a 403?" — with the surviving answer.

*Evidence:* `docs/` is derived from the specs (`verify-the-documentation-surface`, archived
2026-08-28, established that a derived page disagreeing with a spec is a bug in the page). The
amended `authorization` requirement is what these passages must now say.

### `RegistrationDocumentationTests.AnObsoleteRegistrationIsNotRequiredToCarryOne` stays

After the removals it asserts over an empty set and passes vacuously. It is kept: it encodes the rule
that a deprecated registration does not need a worked example, which applies to the next deprecation
whenever there is one. Deleting it would mean re-deriving that rule later. A vacuous pass is the
correct result for "there are currently no deprecated registrations".

## Risks / Trade-offs

**A consumer is still on a removed member and discovers it at the `4.0.0` upgrade.** → That is the
intended mechanism, and it fails at compile time with the successor named in the error. The migration
is a substitution per call site, stated in the proposal and repeated in the CHANGELOG. The two
snapshot overloads need one extra argument the caller can compute from the aggregate type it is
already holding.

**A consumer implements `ISnapshotRepository` themselves and their implementation stops matching.**
→ Removing an interface member breaks an implementor in the forgiving direction: the two members
become dead code in their class, and the compiler does not complain about an extra method. They
delete them at leisure. Called out in the CHANGELOG so it is not a surprise.

**A host relied on the bare 403 and does not want an RFC 7807 body.** → It must register
`AddStrataraProblemDetails()` or map `AuthorizationException` and `TenantAccessDeniedException`
itself. Both exception types are declared in `Stratara.Abstractions`, so a host's own handler catches
them without referencing the mediator or infrastructure packages — the escape hatch the
`tenant-isolation` spec already guarantees. This is the only removal where the successor is not
signature-for-signature equivalent, so it gets its own CHANGELOG paragraph rather than a table row.

**The `S1133` count does not reach zero.** → It reaches zero only if `leave-no-analysis-issue-open`
has shipped and no new `[Obsolete]` appeared meanwhile. The verification task checks the gate reports
zero violations, not merely zero `S1133`; anything else means something in the other change did not
land, and that is worth knowing at this point rather than later.

## Migration Plan

The change ships inside `4.0.0` alongside `require-an-explicit-tenant-selection` and
`anchor-event-subject-to-the-stream`. It carries no runtime migration of its own — no data moves, no
configuration changes, nothing to roll forward. A consumer's migration is a compile-and-fix pass, and
the compiler enumerates the sites.

Ordering against the other two `4.0.0` changes does not matter: this one touches
`ISnapshotRepository`, the Npgsql registration and the 403 middleware, none of which the other two
go near.
