## Context

See `proposal.md` — Why. The current code:

- **The registrations.** `AddStrataraAggregateGrains` (`src/Stratara.Orleans/DependencyInjection/OrleansAggregateServiceCollectionExtensions.cs:32-44`)
  guards its behaviour with an `Any` on the implementation type (`:35-38`) and uses `TryAdd` for the
  rest; `AddStrataraOrleansCommandDispatcher` (`:84-105`) is `TryAdd` and a replace that removes the
  previous registration (`:113-131`); `ConfigureStrataraHeavyWork` (`:148-154`) adds one more
  `Configure` per call, which applies the same delegate again. `AddStrataraProjectionGrains` and
  `AddStrataraSagaGrains` (`OrleansProjectionServiceCollectionExtensions.cs:45-64,88-105`) call
  `services.AddScoped<INudgeTarget, …>()` unguarded (`:59`, `:102`) while the store-reader core, the
  truncator decoration and the process timers are guarded (`:114-117`, `:59-62` of
  `ReplayCheckpointReset.cs`, `:148-151`). `AddStrataraDurableTimers` (`OrleansTimersServiceCollectionExtensions.cs:36-51`)
  is `TryAdd` throughout. `AddStrataraSingletonWork<TWork>` (`OrleansSingletonWorkServiceCollectionExtensions.cs:27-43`)
  calls `services.AddScoped<ISingletonWork, TWork>()` unguarded (`:39`). `RolePlacement.Publish`
  (`Hosting/RolePlacement.cs:58-70`) collects into a set and is idempotent.
- **Who reads the duplicates.** `OrleansEventBundleDispatcher` nudges every `INudgeTarget`
  (`Projections/OrleansEventBundleDispatcher.cs:18`); `StoreReaderSeeding` distincts the consumer names
  (`Projections/StoreReaderSeeding.cs:23`), `ExecutionModelReset` takes the targets as they come
  (`Hosting/ExecutionModelReset.cs:22`); `SingletonWorkStarter` calls `EnsureRunningAsync` once per
  registered work (`Singleton/SingletonWorkGrain.cs:106-113`); `SingletonWorkGrain.ResolveWork` takes the
  first by name (`:87-92`).
- **The check and the publication.** `DurableDirectoryCheck` (`Hosting/DurableDirectoryCheck.cs:31-41`)
  fails at `RuntimeInitialize` when no `IGrainDirectory` is keyed under `GrainDirectories.Durable`.
  `AddStrataraOrleans` (`DependencyInjection/StrataraOrleansSiloBuilderExtensions.cs:33-42`) registers the
  directory and calls `SingletonWorkPlacement.Register(silo)` (`Singleton/SingletonWorkPlacement.cs:69-83`),
  which adds the `SingletonWorkSiloMetadata` singleton, a `Configure<SiloMetadata>` that fills it, and
  `silo.UseSiloMetadata(entries)`. Both placement directors return every silo when that singleton is
  absent (`:32-35`; `Hosting/RolePlacement.cs:185-188`). Orleans exposes `UseSiloMetadata` on
  `ISiloBuilder` only (Orleans.Runtime 10.3.1), so a registration that has an `IServiceCollection` cannot
  publish. `CoHostingTests.AddOrleans` (`tests/Stratara.Orleans.IntegrationTests/Hosting/CoHostingTests.cs:109-120`)
  registers the directory directly (`:116`) and a singleton work (`:106`).
- **The fill and the failure.** `SingletonWorkSiloMetadata.Fill` (`SingletonWorkPlacement.cs:90-99`)
  creates a scope and resolves every `ISingletonWork` to read `Name`, at the first resolution of
  `IOptions<SiloMetadata>` — inside the silo's start. `SingletonWorkGrain.RunOnceAsync` (`:80-85`) awaits
  the work with no catch; a grain timer callback that throws is logged by the runtime under its own
  category and the timer continues. `ISingletonWork` (`src/Stratara.Abstractions/Abstractions/Singleton/ISingletonWork.cs`)
  has `Name`, `Period`, `RunAsync`; the name is an instance property.
- **Precedents.** `TimerPortsStartupCheck` (`Timers/TimerPorts.cs:52-91`) fails the start naming the
  ports that do not compose; `IntentStoreStartupCheck` (`Aggregates/IntentStoreStartupCheck.cs`) names
  the missing registration and the call that provides it.

## Goals / Non-Goals

**Goals:**
- Every registration of the model is idempotent, and a test says so for each.
- A silo that cannot be placed on correctly does not start, and says why.
- A failing singleton work is visible under a framework event, and a work can be registered without
  being constructed while the silo starts.

**Non-Goals:**
- Publishing metadata from an `IServiceCollection` (D2).
- Retrying a failed run or backing it off; the period is the retry.
- Changing `ISingletonWork`. `Name` stays an instance property, so a consumer's work compiles.

## Decisions

### D1 — The wake-up targets and the work are guarded like the rest

`AddStrataraProjectionGrains` and `AddStrataraSagaGrains` add their `INudgeTarget` only when none of
that implementation type is registered; `AddStrataraSingletonWork<TWork>` adds `TWork` only when no
`ISingletonWork` descriptor has that implementation type; `ConfigureStrataraHeavyWork` keeps adding a
`Configure` per call, because two different delegates are two settings and the same delegate twice is
the same value — that is what `AddOptions().Configure` means everywhere in the framework. The others
are already idempotent; a unit test per registration calls it twice and asserts the descriptor counts
of everything it adds, so a regression is caught where it is made.

*Rejected: `TryAddEnumerable` for the targets and the work.* It keys on service and implementation
type and would do the same; the explicit `Any` reads as the guard it is and matches the file's other
guards.

Evidence: `OrleansProjectionServiceCollectionExtensions.cs:59,102,114-117,148-151`;
`OrleansSingletonWorkServiceCollectionExtensions.cs:39`; `OrleansAggregateServiceCollectionExtensions.cs:35-38`.
Test: `tests/Stratara.Orleans.Tests/RegistrationIdempotencyTests.cs` — seven registrations, each called
twice, descriptor counts equal to one call's.

### D2 — The start-up check fails a silo that hosts a role or work without publishing it

`DurableDirectoryCheck.CheckAsync` gains a second condition after the directory: when the composition
registered a `PublishedRoles` with at least one role, or any `ISingletonWork`, and no
`SingletonWorkSiloMetadata` is registered, it logs the existing *DirectoryCheckFailed* (117_107)
with the same category and throws naming the roles (by their registration) and the works (by their
registered names — from the registrations, D4, or from the descriptors' implementation types where
no name was given) and `AddStrataraOrleans` as the call that publishes them. A silo with the directory
and neither a role nor a work — an API host as a silo with the dispatcher only — passes, as today.

Why fail rather than publish: Orleans' `UseSiloMetadata` is an `ISiloBuilder` extension and the
`ISiloBuilder` is not a service, so a registration holding an `IServiceCollection` cannot publish
without re-implementing the runtime's registration; and a silo that is placed on as if it hosted
every role is the failure #107 closed — the only honest answer for a silo that would reintroduce it is
not to start. The message says what to change, and the change is one line.

*Rejected: a warning at start.* The misplacement shows up later, on another silo, as *not registered
on this silo* — the shape of finding R5-Tim-001 — with the warning long scrolled away.
*Rejected: filtering nothing on such a silo but excluding it from others' placements.* The other
silos cannot see that it publishes nothing on purpose.

Evidence: `DurableDirectoryCheck.cs:21-41`; `RolePlacement.cs:57-70,178-193`; `SingletonWorkPlacement.cs:25-40,69-83`;
`StrataraOrleansSiloBuilderExtensions.cs:33-42`; Orleans.Runtime 10.3.1 `SiloMetadataHostingExtensions`
(four `ISiloBuilder` overloads, no `IServiceCollection` one); `CoHostingTests.cs:106,116`. Tests: a
unit test on the check with a composition that registers a role and no metadata, asserting the
message names the role's registration and `AddStrataraOrleans`; an integration test in
`DurableDirectoryCheckTests` that registers the directory directly plus one singleton work and
expects the start to fail naming the work and the call, and one that registers the directory directly
plus the dispatcher only and expects the silo to start; `CoHostingTests` switches to `AddStrataraOrleans`.

### D3 — A failing run is logged with an event of the framework's, and the work goes on

`SingletonWorkGrain.RunOnceAsync` catches every exception but `OperationCanceledException`, logs
*SingletonWorkFailed* — error, with the work's name and the exception, the next free id in
`LogEvents.Orleans` (117_114 as of #109; renumber at implementation if another change has taken it) —
and returns, so the grain timer's next tick runs the work again at its period as it does today after
the runtime's warning. The grain takes an `ILogger<SingletonWorkGrain>`. `OutboxDrainWork` and a
consumer's work are treated alike.

*Rejected: rethrowing after logging.* The runtime would log it a second time under its own category;
the behaviour — the next tick runs — is the same either way.
*Rejected: a counter.* One failing work is an alert on its event; a fleet-wide rate is what the
event's count gives an operator without a new instrument name to keep.

Evidence: `SingletonWorkGrain.cs:57-85`; `OrleansLog.cs` (the shape); `docs/guides/operate-the-orleans-execution-model.md:146-148`
(the list to route). Test: an integration test in `SingletonWorkTests` with a work that throws on its
first run and records its second, asserting the event and the second run.

### D4 — The name is registered, the metadata is filled from the registration, and the work is first constructed when the silo is active

`AddStrataraSingletonWork<TWork>(string name, Action<SingletonWorkOptions>? configure = null)` records
the name beside the work in a `SingletonWorkRegistrations` singleton the metadata fill reads instead
of constructing the work; `SingletonWorkStarter`, which constructs every work at the silo's `Active`
stage to ask its grain to run, compares each work's `Name` with its registered name and throws naming
both where they differ — the runtime fails the silo's start on that stage. `OutboxDrainWork` exposes
its name as a constant (`OutboxDrainWork.WorkName`) so the guide's line reads
`.AddStrataraSingletonWork<OutboxDrainWork>(OutboxDrainWork.WorkName)`. The existing overload without
a name keeps constructing the work at the fill, as today, and a construction that throws there is
wrapped in a failure naming the work's type and that it was constructed at the silo's start to publish
its name — register it with its name to avoid that.

Why two overloads rather than one path: the name is an instance property, so without a registered
name it can only come from an instance; making the name mandatory breaks every host; a static or an
attribute on the work would be a second way to name it that the interface does not know. The named
overload is the one the guide shows; the unnamed one stays for the works that do not care.

*Rejected: keying the grain and the metadata by the work's type.* The grain key would change for
every work, the keep-alive reminders of the old keys would re-activate grains that find no work under
their name and throw on every tick, and a consumer's `Name` would stop meaning what its documentation
says.
*Rejected: filling the metadata when the silo is active.* The runtime resolves the metadata options
when the silo starts and other silos fetch it once it is a member; a fill after that is a race the
placement loses.

Evidence: `SingletonWorkPlacement.cs:86-100` (the fill), `SingletonWorkGrain.cs:101-114` (the starter),
`OutboxDrainWork.cs:24` (the name), `ISingletonWork.cs:12`; Orleans.Runtime 10.3.1 `SiloMetadata` (an
options object). Tests: a unit test that a composition with a named work fills the metadata without
resolving `ISingletonWork` (a work whose constructor throws); a unit test on the starter's check with a
mismatched name; an integration test in `SingletonWorkTests` with a named work whose constructor
records the time it was first constructed, asserting it is after the silo became active and that the
work runs.

## Risks / Trade-offs

- [A host that registered the directory by hand and hosts a role stops starting on upgrade] → intended
  and stated in the changelog as a behaviour change; the message names the one-line fix, and the host
  was being misplaced on before.
- [A consumer registered a work twice on purpose to run it twice] → the grain is keyed by name and
  runs once anyway; two registrations never ran it twice.
- [The unnamed overload still constructs the work at start] → documented on both overloads; the
  named one is the one the guide and the README show.
- [Two changes allocate the same log event id] → assigned at implementation as the next free one in
  the band; the schema page is the record.

## Migration Plan

Patch release. New public surface: the named `AddStrataraSingletonWork<TWork>` overload,
`OutboxDrainWork.WorkName`, one log event id. Behaviour change at start for a silo that hosts a role
or work without `AddStrataraOrleans`: it fails naming the call. No schema change. Rollback: nothing
to undo.
