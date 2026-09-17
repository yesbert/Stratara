## Context

See `proposal.md` — Why. The current code, on `main` after #109:

- **Forwarding.** `AggregateGrainBehavior.HandleAsync` (`src/Stratara.Orleans/Aggregates/AggregateGrainBehavior.cs:57-58`)
  awaits the call the send lane made to `IAggregateGrain.ExecuteAsync`; nothing in `src/` sets
  `MessagingOptions.ResponseTimeout`, so the runtime's thirty seconds bound that wait. The runner is a
  one-way call since #107 (`AggregateGrain.cs`, D1 of `close-the-round-5-execution-gaps`), so the
  handler runs to the end after the caller's `TimeoutException`. `LongOrderTests` already runs a silo
  with `ResponseTimeout = 2 s`. The operations guide's section *The response timeout* names the
  setting and the heavy-work alternative (`docs/guides/operate-the-orleans-execution-model.md:69-78`).
- **Authorization on resume.** `CommandExecution.InvokeAsync` (`AggregateGrain.cs:294-310`) sets the
  session from the envelope and dispatches through `IMediator`, so an `AuthorizingMediator` decorator
  runs on the silo with whatever `IAuthorizationProvider` the host registered.
  `IAuthorizationProvider`'s own example reads `IHttpContextAccessor.HttpContext?.User`
  (`src/Stratara.Abstractions/Authorization/IAuthorizationProvider.cs:17-31`); on a silo there is no
  request, the example returns `false`, `AuthorizationException` is thrown, `IntentLease.RecordFailureAsync`
  records it, and after `MessageRetryOptions.MaxDeliveryAttempts` the command is kept.
  `MembershipAuthorizationProvider` (`src/Stratara.Identity.EntityFrameworkCore/MembershipAuthorizationProvider.cs:23-24`)
  answers from `ISessionContextProvider` and works on both paths. `docs/guides/enforce-tenant-isolation.md:79-80`
  already says the tenant behaviour runs worker-side without `HttpContext`; nothing says it for the
  intent path. `IntentPipelineTests` is the test class that runs a recorded command through the pipeline.
- **The record.** `CommandIntentStore.RecordAsync` (`src/Stratara.Orleans.EntityFrameworkCore/Intents/CommandIntentStore.cs:23-38`)
  opens a context of its own and saves one row. The migration guide's dispatcher row says "records the
  command in the outbox table" (`docs/guides/migrate-to-the-orleans-execution-model.md:419`); the
  RabbitMQ guide says of the bus dispatcher that "a command has no commit to be atomic with"
  (`docs/guides/outbox-setup-rabbitmq.md:194`).
- **Full replay.** `ProjectionReplayWorker.RunReplayAsync` (`src/Stratara.Projections/Services/ProjectionReplayWorker.cs:70-93`)
  activates the replay state, truncates through `IProjectionViewTruncator`, applies every entry in
  sequence order through `IProjectionManager`, and deactivates. `ReplayCheckpointResetTruncator`
  (`src/Stratara.Orleans/Projections/ReplayCheckpointReset.cs:27-51`) pauses the grains, writes
  position 0 for every projection × partition, truncates, resumes. `StoreReaderGrain.Suspended`
  (`StoreReaderGrain.cs:52`) is `IsReplayActive`, so the readers wait and then read from 0.
  `ReplayWithStoreReadersTests` asserts the views are refilled and the checkpoints advance; the PoC
  read context has an unguarded running total (`CounterTotalsProjection`,
  `tests/Stratara.Orleans.Scenarios/Projections/AuditProbe.cs:35-57`) beside the version-guarded view.
- **Singleton work.** `SingletonWorkGrain` (`src/Stratara.Orleans/Singleton/SingletonWorkGrain.cs:25-72`)
  runs a grain timer under the durable directory and keeps a reminder of `KeepAlivePeriod`. Orleans
  suspects a silo after `NumMissedProbesLimit` probes of `ProbeTimeout` (3 × 5 s by default), declares
  it dead once `NumVotesForDeathDeclaration` votes are in (2, or what a smaller cluster can muster),
  removes its directory entries, and the reminder brings the grain up elsewhere; the declared silo
  learns of the declaration at its next table refresh (`TableRefreshTimeout`, 60 s) or probe and
  terminates itself. Until then its timer runs. `ISingletonWork`'s summary
  (`src/Stratara.Abstractions/Abstractions/Singleton/ISingletonWork.cs:3-7`) says "no two hosts run it
  at the same time". `TwoSiloKillTests` allows three minutes for the takeover under the test profile
  (`PocSilo.cs:56-57`: one-second minimum reminder period, five-second refresh; membership defaults).
- **Hybrid.** `ReplaceBundleDispatcher` (`src/Stratara.Orleans/DependencyInjection/OrleansProjectionServiceCollectionExtensions.cs:154-170`)
  removes the last `IEventBundleOutboxDispatcher` descriptor and wraps it only when
  `existing?.ImplementationType is { } innerType`; a factory or instance descriptor is removed and lost,
  and `hybrid: true` with no descriptor at all yields the grain-only `OrleansEventBundleDispatcher`
  (`inner` null, `OrleansEventBundleDispatcher.cs:55-58`). The same class has `Instantiate(IServiceProvider, ServiceDescriptor)`
  (`:124-139`), which handles all three descriptor shapes and is what `ReplayCheckpointResetTruncator.Decorate`
  uses. The framework's own registration is by type (`OutboxServiceCollectionExtensions.cs:39`), so the
  shipped composites are not affected; a consumer's own dispatcher may be.
- **Package documentation.** `GrainDirectories` (`src/Stratara.Orleans/GrainDirectories.cs:3-6,9-15`)
  says "proof of concept" twice in XML that is published with the package and on the API site.
  `src/Stratara.Orleans.EntityFrameworkCore/README.md:17-27` names all four registrations of R5-Mig-012
  since #107; its quick start (`:31-36`) shows the read side only.

## Goals / Non-Goals

**Goals:**
- Every limit the round-5 audit found by design is stated where a consumer meets it: in the
  specification as a guarantee with its condition, in the guide a team reads for that step, and in
  the XML of the member it concerns.
- Each stated limit has a test that shows the behaviour as stated, where a test can show it.
- The one silent failure in the group — a bus dispatcher lost under `hybrid: true` — stops being
  silent.

**Non-Goals:**
- Changing the response timeout's semantics, waiting past it, or returning a result for a forwarded
  command after the timeout. The timeout is the host's setting and bounds every call.
- Making the record atomic with the caller's unit of work. The recording dispatcher has no
  transaction of the caller to join; the bus dispatcher does not either.
- A single pass under a full replay. The readers' pass from the beginning is what the commit-order
  guarantee stands on; the replay worker's sequence-order pass is the replay's own.
- A lease or fence that stops a declared-dead silo's singleton work at once. Orleans' membership is
  the lease; the window is its.
- A partition test for the overlap window. Two silos that both believe they are the cluster cannot be
  produced with the harness the suite has; the window is stated from Orleans' membership protocol and
  the settings that bound it, and the takeover the suite does prove is recorded with its profile.

## Decisions

### D1 — The caller side of the response timeout is a sentence in the guide, a scenario, and a test

The operations guide's section *The response timeout* gains the consequence: a forwarded command whose
handler outlasts the timeout fails its caller with a timeout while the handler runs to the end and
appends; the caller does not retry on a timeout — the retry would run the command again, and on an
aggregate that already appended it ends in a concurrency conflict at best. The three ways out are
listed together: mark the command `IHeavyCommand`, size `MessagingOptions.ResponseTimeout`, or
dispatch through `ICommandOutboxDispatcher` where the caller need not wait for the result. The
requirement's sentence on the timeout gets the same statement.

*Rejected: cancelling the handler when the caller's wait ends.* The grain's turn is the aggregate's
only writer; ending it half-way from the caller's side is the inconsistency the model exists to
prevent, and cancellation does not reach a handler on a grain path today (R5-Cmd-011, its own item).
*Rejected: an idempotency key on the forwarded call so a retry is deduplicated.* Public surface for
a case the recording dispatcher already covers.

Evidence: `AggregateGrainBehavior.cs:57-58`; `LongOrderTests` (a silo with `ResponseTimeout = 2 s`);
Orleans `MessagingOptions.ResponseTimeout`. Test: a forwarded command with a four-second handler under a
two-second timeout — the caller observes `TimeoutException`, the handler's start and completion count
one each, the aggregate's version is incremented once.

### D2 — A session-driven provider is named as what the intent path needs

The requirement says a resumed command is authorized from the session recorded with it, on a silo,
and that the documentation names a provider that answers from the current web request as one that
refuses every resumed command. The migration guide's dispatcher row and the authorization guide say
which providers work on both paths: `MembershipAuthorizationProvider` and any provider that reads
`ISessionContextProvider`; the example on `IAuthorizationProvider` gets a remark that it is the
request-side shape and that a host which records commands needs the session-side one. The failure a
request-bound provider produces is already visible as `117_112` per attempt and `117_104` on the keep.

*Rejected: carrying the caller's claims in the envelope so a request-bound provider can answer.* The
envelope carries the session; the session is the framework's notion of "who" (actor and subject), and
a claims principal is the web framework's. Serialising one into the record would tie the record to
ASP.NET.
*Rejected: skipping authorization on resume because it passed at dispatch.* Enqueue-time
authorization is optional; the mediator's is the one every path shares.

Evidence: `AggregateGrain.cs:294-310`; `AuthorizingMediator.cs:33-44`; `IAuthorizationProvider.cs:17-31`;
`MembershipAuthorizationProvider.cs:23-24`; `IntentPipelineTests` (the composition to reuse). Test: a
`[RequireRole]` command dispatched under a session the provider recognises, the host killed before the
handler ran, the resumption handled; the same command under a provider that answers from an absent
request, kept with `attempts=3`.

### D3 — The record's transaction is one sentence where the record is introduced

The concept page's *An accepted command is not lost* and the migration guide's dispatcher row say
that the record is committed on its own, before the dispatch returns, and not with anything the
caller writes; a caller that needs its own writes and the command to stand or fall together writes
its own facts first and dispatches after the save, which is the order the bus path has always needed.
The requirement gains the sentence and a scenario.

*Rejected: enlisting the record in the caller's `IWriteUnitOfWork` when one is open.* The dispatcher
takes no transaction, the bus dispatcher takes none, and a record inside the caller's transaction
would be visible to the drain only after the caller's commit while the hand-over has already happened.

Evidence: `CommandIntentStore.cs:23-38`; `outbox-setup-rabbitmq.md:194`. Test: a dispatch inside a
scope whose write unit of work is started and disposed without a save; the command runs and its own
append commits.

### D4 — The two passes of a full replay are stated, with the single rebuild as the alternative

The requirement says a full replay returns every store-reading projection's checkpoint to the
beginning before the read models are emptied, that the replay's pass is followed by the readers' pass,
and that the projection applies the store twice and is correct because it applies idempotently. The
migration guide's replay section and the concept page's cost list say the same in plain words and name
`IProjectionRebuilder.RebuildAsync` as the way to re-read one read model once.

*Rejected: suppressing the readers' pass by seeding the checkpoints at the head when the replay ends.*
The replay worker reads by sequence number; the readers' pass is the commit-order pass, and it is the
one that catches an entry the sequence-order pass could have missed under an interleaved commit.
*Rejected: suppressing the replay worker's pass for store-reading projections.* The replay serves the
bus-fed projections of the same host, and `IProjectionManager` does not know which projection reads
the store.

Evidence: `ProjectionReplayWorker.cs:70-93,147-184`; `ReplayCheckpointReset.cs:27-51`;
`StoreReaderGrain.cs:52`; `ReplayWithStoreReadersTests`; `CounterTotalsProjection`. Test: the existing
replay test additionally asserts that the unguarded total counts each fact twice after the replay and
the catch-up, and the guarded view once.

### D5 — The overlap window and the failover latency are stated from the settings that bound them

`ISingletonWork`'s summary says the work runs in one place while the cluster agrees on its membership,
that a silo the cluster has declared dead may still be running it until it learns of the declaration,
and that a run therefore tolerates an overlapping run on another host. The operations guide gets a
section *Singleton work under a suspected death* that walks the sequence: probes missed
(`NumMissedProbesLimit × ProbeTimeout`), votes (`NumVotesForDeathDeclaration`), the reminder brings the
grain up elsewhere on its next tick (`RefreshReminderListPeriod`, `SingletonWorkOptions.KeepAlivePeriod`),
the declared silo stops itself at its next refresh (`TableRefreshTimeout`) — so a failover completes
within the sum of those with the defaults in the order of two to three minutes, and the overlap lasts
at most one refresh period plus one run. The two-silo kill test's bound of three minutes under the test
profile is named as the measurement the suite has. The requirement gains the window and a scenario
whose verification is the documentation.

*Rejected: a fencing token the work must present to the store.* The framework's own drain claims
intents with a compare-and-set, which is the fence where one is needed; a consumer's work has its own
store.
*Rejected: shortening the membership defaults in `AddStrataraOrleans`.* They are the host's; the guide
already says the `IAmAlive` settings do not change what a joiner waits for.

Evidence: `SingletonWorkGrain.cs:25-72`; `ISingletonWork.cs:3-7`; Orleans `ClusterMembershipOptions`
(defaults from the runtime's XML documentation: probe timeout 5 s, three missed probes, two votes,
table refresh 60 s); `TwoSiloKillTests.cs:15,37-39`; `PocSilo.cs:56-57`.

### D6 — The hybrid shape wraps any registration and refuses a host with none

`ReplaceBundleDispatcher` wraps the removed descriptor through `Instantiate`, so a bus dispatcher
registered by type, factory or instance is kept with its lifetime, as the replay truncator's wrapper
already does; `hybrid: true` with no `IEventBundleOutboxDispatcher` registered throws
`InvalidOperationException` at registration naming `hybrid` and `AddOutboxDispatcher` (the framework's
registration that supplies one). The requirement gains both as scenarios.

*Rejected: documenting the limitation and leaving the code.* A host that asked for bundles on the bus
and gets none loses the bus consumers it kept `hybrid: true` for, silently, during the one window —
the rollout — where it matters; the fix reuses a helper in the same class.
*Rejected: a warning log instead of a throw for the missing dispatcher.* Every other misregistration
of the model fails at start naming what is missing.

Evidence: `OrleansProjectionServiceCollectionExtensions.cs:124-139,154-170`; `OrleansEventBundleDispatcher.cs:16-31`;
`OutboxServiceCollectionExtensions.cs:39`. Test: unit tests on a service collection with a
factory-registered dispatcher (the inner is resolved and receives the bundle) and with none (the
registration throws naming both).

### D7 — The package documentation says what ships

`GrainDirectories`' XML loses "proof of concept" in both places and says what the durable directory is
for and that Redis is the directory the guides show. The `Stratara.Orleans.EntityFrameworkCore` README
is verified against R5-Mig-012 — it names the four members since #107 — and its quick start gains the
write side (`AddStrataraIntentStore<AppWriteDbContext>()` beside `AddStrataraOrleansCommandDispatcher()`).
`llms.txt` and the changelog carry the hybrid change and the documented limits.

Evidence: `GrainDirectories.cs:3-16`; `README.md:17-36`; `close-the-round-5-execution-gaps` task 9.2.

## Risks / Trade-offs

- [A host that relied on `hybrid: true` silently running grain-only] → it now fails at registration
  with a message naming `AddOutboxDispatcher`; passing `hybrid: false` restores what it had. Named in
  the changelog.
- [A consumer's factory-registered dispatcher now runs beside the grains] → that is what the host asked
  for; a host that did not want it never passed `hybrid: true`.
- [The failover latency stated in the guide is derived, not measured under production defaults] → the
  guide says which settings it is derived from and names the suite's measurement under the test
  profile; a team that needs a number for its own settings has the formula.
- [A stated limit reads as a defect] → each is stated with why it is the shape it is and what a
  consumer does about it.

## Migration Plan

Patch release. No schema change, no new public member. `ISingletonWork`'s XML summary is reworded;
`GrainDirectories`' XML is reworded. Behaviour change only under `hybrid: true`: a factory- or
instance-registered bus dispatcher is kept; a missing one fails the registration. Rollback: none
needed.
