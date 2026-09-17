# make-the-role-registrations-idempotent-and-their-failures-visible

> **Status:** approved (owner, 2026-09-17 — recorded at the owner's request)

## Why

The round-5 audit walked the execution model's registrations the way a host composed from several
extension methods calls them — more than once, in either order, and not always through the silo
builder — and found three gaps between what the guide says and what the composition does (findings
**R5-Tim-009**, **R5-Tim-010** and **R5-Tim-011**, all low):

- **A second call of a role registration adds a second copy of what it registers.**
  `AddStrataraProjectionGrains` and `AddStrataraSagaGrains` add their wake-up target on every call, so
  a host that calls one twice — a composite of its own that wraps it, or two feature modules that both
  adopt the role — wakes every projection twice per bundle and lists every consumer twice to the
  seeding and the reset; `AddStrataraSingletonWork<TWork>` registers the work twice, so the starter
  asks its grain to run twice and the metadata names it twice. The guide says every registration
  "applies idempotently"; the aggregate role's behaviour and the timers do, the rest do not.
- **A silo that registers the directory itself passes the start-up check and publishes nothing.**
  `AddStrataraOrleans` does two things: it registers the storage-backed directory and it publishes,
  in the silo's metadata, the roles and singleton work the composition registered. A host that
  registers the directory directly under the model's name — the co-hosting test does exactly this —
  passes the directory check, because the check looks for the directory only, and publishes no
  metadata; its placement filters then see every silo, so in a cluster whose silos register
  different roles its aggregates, projections and singleton work are placed on silos that lack them
  and fail there, which is the defect #107 closed for silos that publish.
- **A singleton work that fails is a runtime timer warning, and every work is constructed before the
  host is up.** A run of an `ISingletonWork` that throws is logged by the Orleans grain timer under
  the runtime's own category, with no event id of the framework's and nothing an operator's alerting
  keys on; and to publish the works' names the silo constructs every registered work from a scope
  while its metadata is built — at silo start, before any hosted service registered after the silo
  has run — so a work whose constructor needs something the host initialises later fails the silo
  start with an options error that names neither.

## What Changes

- **Every role registration is idempotent.** A second call of `AddStrataraAggregateGrains`,
  `AddStrataraProjectionGrains`, `AddStrataraSagaGrains`, `AddStrataraDurableTimers`,
  `AddStrataraOrleansCommandDispatcher`, `ConfigureStrataraHeavyWork` and `AddStrataraSingletonWork<TWork>`
  leaves the composition as one call leaves it: one wake-up target per role, one work per type, one
  behaviour, one starter, one check.
- **A silo that hosts a role or singleton work without publishing it fails at start naming what is
  missing.** The start-up check that today looks for the directory also looks for the publication
  and, where the silo registered a role or a work but nothing publishes it, fails with a message
  naming the roles and works it found and the call that publishes them. A silo that registers nothing
  of the model — an API host as a silo with the dispatcher only — is unaffected.
- **A singleton work that fails is logged with an event of the framework's.** A run that throws is
  logged at error with the work's name and the exception, under a new id in the Orleans band, and the
  work's next run goes ahead as before; the operate guide lists the event among those to route.
- **A work is not constructed to learn its name.** `AddStrataraSingletonWork<TWork>` takes the name the
  work publishes under at registration, so the silo's metadata is filled without constructing
  anything; the work's `Name` is compared with the registered one when the silo is active, where the
  work is first constructed, and a mismatch fails the start naming both. The overload without a name
  keeps working and keeps constructing the work when the metadata is built, with a failure there
  naming the work and why it was constructed.
- **Consumer-visible effects:** a silo that registered the directory by hand and hosts a role fails at
  start where it used to start and misplace; a second registration no longer doubles anything; one
  new log event id; one new overload. No schema change. Versioning: patch.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: *Work that must happen once happens once per cluster* — a run that fails is
  logged with an event of its own and the next run goes ahead; a work registered with its name is not
  constructed before the silo is active; new scenarios.
- `orleans-execution`: *The execution model can be adopted per role beside the bus workers* — a
  registration called twice registers once; a silo that hosts a role or work without publishing it
  fails at start naming what is missing; new scenarios.

## Impact

- `Stratara.Orleans` — `src/Stratara.Orleans/DependencyInjection/OrleansProjectionServiceCollectionExtensions.cs`
  (the wake-up targets), `src/Stratara.Orleans/DependencyInjection/OrleansSingletonWorkServiceCollectionExtensions.cs`
  (the work, the name overload), `src/Stratara.Orleans/DependencyInjection/OrleansAggregateServiceCollectionExtensions.cs`
  and `OrleansTimersServiceCollectionExtensions.cs` (verified idempotent, tests only),
  `src/Stratara.Orleans/Hosting/DurableDirectoryCheck.cs` (the publication check),
  `src/Stratara.Orleans/Singleton/SingletonWorkPlacement.cs` (the fill from registered names),
  `src/Stratara.Orleans/Singleton/SingletonWorkGrain.cs` (the failure log, the name check),
  `src/Stratara.Orleans/Diagnostics/OrleansLog.cs`.
- `Stratara.Diagnostics` — `src/Stratara.Diagnostics/LogEvents.cs`, one id in `LogEvents.Orleans`.
- `docs/guides/operate-the-orleans-execution-model.md` (*The grain directory*: the publication;
  *What to watch*: the event), `docs/guides/migrate-to-the-orleans-execution-model.md` (the
  registrations are idempotent, the name overload), `docs/reference/di-extensions-cheatsheet.md`,
  `docs/reference/log-events-schema.md`, `src/Stratara.Orleans/README.md`, `CHANGELOG.md`, `llms.txt`.
- Tests: `tests/Stratara.Orleans.Tests/` — every registration called twice, the fill from registered
  names, the name mismatch; `tests/Stratara.Orleans.IntegrationTests/Hosting/` — a silo with the
  directory registered directly and a role fails at start naming the call (`CoHostingTests` moves to
  `AddStrataraOrleans`); `tests/Stratara.Orleans.IntegrationTests/Singleton/` — a failing work logs the
  event and runs again.
- As implemented: log event ids `117_119` (a failing run) and `117_120` (a silo that does not publish — a new id
  rather than `117_107`); the registrations of the works in `src/Stratara.Orleans/Singleton/SingletonWorkRegistrations.cs`;
  tests `tests/Stratara.Orleans.Tests/RegistrationIdempotencyTests.cs`, `DurableDirectoryCheckTests.cs`,
  `SingletonWorkMetadataTests.cs`.
- Versioning: patch.
