# Document the execution model's operating limits

> **Status:** approved (owner, 2026-09-17 — recorded at the owner's request)

## Why

The round-5 audit of 2026-09-16 left a group of findings that are not defects: limits the execution
model has by design, which nothing states. A team that reads the specification, the two guides and
the package READMEs today acts on promises the code keeps only under conditions it is never told.
Closes R5-Cmd-006, R5-Cmd-009, R5-Cmd-013, R5-Rdr-005, R5-Tim-004, R5-Mig-012 and R5-Mig-014.

- **A forwarded command whose handler outlasts the response timeout is reported failed to its caller
  and commits all the same** (R5-Cmd-006). The forwarding behaviour awaits the grain call
  (`AggregateGrainBehavior.cs:57-58`); the runtime ends that wait after `MessagingOptions.ResponseTimeout`
  while the aggregate's runner, a one-way call since #107, runs the handler to the end. The operations
  guide names the timeout as a setting the host sizes; it does not say that a caller who sees the
  timeout and retries appends a second time.
- **A resumed command is authorized on a silo, from the session recorded with it** (R5-Cmd-009). The
  resumption goes through the mediator (`AggregateGrain.cs:294-310`), so a host that registered
  `AddAuthorizingMediator` with a provider that reads the current web request — the shape the
  provider's own example shows — refuses every resumed command; after `MaxDeliveryAttempts` each one
  is kept. Nothing says a session-driven provider is what the intent path needs.
- **The record of an accepted command is committed in a transaction of its own** (R5-Cmd-013), one
  statement on a context of its own (`CommandIntentStore.cs:26-37`). The migration guide says the
  dispatcher "records the command in the outbox table", and "outbox" reads as "atomic with what the
  caller writes" — it is not, and a caller whose own unit of work fails after the dispatch has a
  recorded command that runs.
- **A full replay applies the whole store twice to store-reading projections** (R5-Rdr-005): the replay
  worker applies every entry in sequence order (`ProjectionReplayWorker.cs:147-184`), the wrapped
  truncator returned every checkpoint to the beginning first (`ReplayCheckpointReset.cs:40-43`), and
  the readers re-read from there once the replay ends (`StoreReaderGrain.cs:52`). It is correct because
  projections apply idempotently, and the second pass is the one the reader's order guarantee stands
  on. The guide's replay section says none of it.
- **Singleton work runs on two hosts for the failover window** (R5-Tim-004). `ISingletonWork`'s summary
  promises "no two hosts run it at the same time"; a silo the cluster has declared dead while it still
  runs keeps its grain timer going until it learns of the declaration and stops itself, while the
  keep-alive reminder has already brought the grain up elsewhere (`SingletonWorkGrain.cs:43-72`). The
  framework's own drain tolerates that; a consumer's work is not told to. How long a failover takes is
  named nowhere, and the two-silo kill test proves the takeover only under the test profile.
- **`hybrid: true` keeps a bus dispatcher only when it was registered by implementation type**
  (R5-Mig-014, `OrleansProjectionServiceCollectionExtensions.cs:161-165`); a dispatcher registered by
  factory or instance is removed and not wrapped, and a host with no bus dispatcher at all gets the
  grain-only shape without a word. In both cases the host asked for bundles on the bus and gets
  none. The XML documentation of `GrainDirectories`, a published type, still calls the model a "proof
  of concept" (`GrainDirectories.cs:4,12`).
- **The `Stratara.Orleans.EntityFrameworkCore` README** (R5-Mig-012) names `AddStrataraIntentStore`,
  `AddStrataraPortableCounterReader`, `AddStrataraExecutionModelReset` and `PartitionCounterBackfill`
  since #107 (task 9.2); the tracker still lists the finding. Its quick start shows only the read side.

## What Changes

- **The response timeout's caller-side consequence is stated.** The operations guide says that a
  forwarded command whose handler outlasts `MessagingOptions.ResponseTimeout` fails its caller with a
  timeout while the handler runs to the end and commits, so a caller must not retry on a timeout; the
  three ways out — mark the command heavy, size the timeout, dispatch through the recording
  dispatcher where the caller need not wait — are named in one place. The specification gains the
  scenario, and a test shows the handler running once and its append committed after the caller's
  timeout.
- **Authorization on the intent path is stated.** The specification says a resumed command is
  authorized from the session recorded with it, on a silo, and that the documentation names a
  provider bound to the current web request as one that refuses every resumed command. The migration
  guide's dispatcher row and the authorization guide say which providers work there; a test runs a
  role-guarded command through a kill and a resumption under a session-driven provider, and shows a
  request-bound provider keeping the command after its attempts.
- **The record's transaction is stated.** The specification says the record is committed on its own,
  not with anything the caller writes; the concept page and the migration guide say so in one
  sentence each; a test dispatches inside a unit of work the caller then abandons and sees the command
  run.
- **The full replay's two passes are stated.** The specification and the guide's replay section say
  that store-reading projections apply the store twice under a full replay and why that is correct,
  and name the single-projection rebuild as the way to re-read once. The replay test counts the
  applications with the unguarded probe.
- **Singleton work's overlap window and failover latency are stated.** The specification says the
  work MAY run on a second host between a death declaration and the declared silo's own stop, that a
  consumer's work SHALL tolerate that, and that the documentation names the latency in terms of the
  membership and keep-alive settings. `ISingletonWork`'s summary says the same; the operations guide
  gets a section that walks a suspected death through the settings that bound it.
- **`hybrid: true` wraps any registration shape, and refuses a host with nothing to wrap.** The
  inner dispatcher is instantiated from its descriptor as the replay truncator's wrapper already does
  (`OrleansProjectionServiceCollectionExtensions.cs:124-139`), so a factory- or instance-registered bus
  dispatcher keeps publishing; `hybrid: true` on a host that registered no bus dispatcher throws at
  registration naming the parameter and the registration that supplies one. The specification gains
  both as scenarios.
- **The package documentation says what ships.** `GrainDirectories`' XML documentation drops "proof of
  concept"; the `Stratara.Orleans.EntityFrameworkCore` README is verified to name the four registrations
  and gains the write-side quick start beside the read-side one.
- **Consumer-visible effects:** a host that passed `hybrid: true` with a factory-registered bus
  dispatcher now publishes bundles where it silently did not; a host that passed `hybrid: true` with no
  bus dispatcher now fails at registration where it silently ran grain-only. No schema change, no new
  public member; one XML summary reworded on a public interface. Versioning: patch.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`:
  - *An accepted command is recorded before the call returns and resumed after a crash* — the
    record is committed on its own; a forwarded command's handler outlasting the response timeout is
    reported as a timeout while it commits, and the documentation says so; a resumed command is
    authorized from its recorded session, and the documentation names the provider shape that cannot;
    three new scenarios.
  - *Projections and sagas read the store in commit order and never miss a committed fact* — a full
    replay applies the store twice to store-reading projections, correctly, and the documentation says
    so; new scenario.
  - *Work that must happen once happens once per cluster* — the overlap window under a suspected
    death and the failover latency are stated, and a consumer's work tolerates the overlap; new
    scenario.
  - *The execution model can be adopted per role beside the bus workers* — the hybrid shape keeps a
    bus dispatcher whatever the shape of its registration and refuses a host with none; two new
    scenarios.

## Impact

- `Stratara.Orleans` — `DependencyInjection/OrleansProjectionServiceCollectionExtensions.cs`
  (`ReplaceBundleDispatcher`), `GrainDirectories.cs` (XML only).
- `Stratara.Abstractions` — `Abstractions/Singleton/ISingletonWork.cs` (XML only).
- `docs/guides/operate-the-orleans-execution-model.md` (the response timeout's caller side; a section
  on singleton work under a suspected death; the full replay), `docs/guides/migrate-to-the-orleans-execution-model.md`
  (the dispatcher row: the record's transaction and the provider shape; the replay section; the hybrid
  paragraph), `docs/concepts/orleans-execution-model.md` (the costs list), `docs/guides/require-permission.md`
  or `docs/guides/enforce-tenant-isolation.md` (the provider on the intent path),
  `src/Stratara.Orleans.EntityFrameworkCore/README.md`, `src/Stratara.Orleans/README.md`, `llms.txt`,
  `CHANGELOG.md`.
- Tests: a forwarded handler past a shortened response timeout; a role-guarded command resumed under
  a session-driven provider and kept under a request-bound one; a dispatch inside an abandoned unit of
  work; the replay's application count; the hybrid wrapper over a factory registration and over none;
  documentation tests.
- Versioning: patch.
