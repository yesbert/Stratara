## Context

See proposal.md, *Why*, for the defect.

Every store reader derives from `StoreReaderGrain`. On activation, the base grain asks the virtual `RetiresAs`. If
the answer is not `StoreReaderRetirement.None`, the grain unregisters its keep-alive reminder and reads nothing.
For `Unregistered` it also calls `DeactivateOnIdle`. It then calls `LogRetirement` and returns
(`src/Stratara.Orleans/Projections/StoreReaderGrain.cs:202-213`). Every call to a retired grain is a no-op, and
`EnsureRunningAsync` is one too.

- `SagaReaderGrain` overrides `RetiresAs` with `Registrations.TypeOf(SagaName) is null ? Unregistered : None`.
  `SagaRegistrations` is a singleton that reads the saga types from the service descriptors once
  (`src/Stratara.Orleans/Sagas/SagaRegistrations.cs`). A saga's name is its type name.
- `ProjectionGrain` does not override `RetiresAs`. It finds its projection in `ResolveProjection`, the first time it
  applies an entry, by resolving every `IProjection` in the entry's scope and comparing
  `IProjectionHandler.GetProjectionName(projection)` with its consumer name. When no projection matches, it throws.
  The throw stalls the partition, and the next tick tries again.
- A projection's name comes from `IProjectionHandler.GetProjectionName(IProjection)`. The default answers the type
  name, but the interface is public and a host can replace the handler. The names the silo starts readers for come
  from the same call: `ProjectionNudgeTarget` is registered scoped, takes `IEnumerable<IProjection>`, and is
  resolved by `StoreReaderGrainStarter` at silo start and by the projection wake-up grain on every wake-up.

## Goals / Non-Goals

**Goals:**

- A projection reader whose projection the silo does not register retires on activation, through the base grain's
  existing path, with no new state.
- The silo decides "registered" by the same names it starts and wakes readers for.

**Non-Goals:**

- Removing an orphaned consumer's checkpoints or reminders other than the reader's own keep-alive (D4).
- Changing the saga reader. Its behaviour is recorded in the spec, and its code stays as it is.
- Keeping a reader off silos that do not register its consumer. Placement stays role-based (see *Risks*).

## Decisions

### D1 — `ProjectionGrain` overrides `RetiresAs`, as the saga reader does

`RetiresAs` answers `Unregistered` when the silo's projection names do not contain the grain's consumer.
`LogRetirement` logs the new event (D3). Everything else is already in the base grain: unregistering the reminder,
reading nothing, counting no stall, no-op calls and the deactivation.

*Alternative rejected:* retiring from inside `ResolveProjection` on the first entry. The reader would read the
store before it retires, and the retirement path would split between activation and apply.

*Evidence:* the implementation of `StoreReaderGrain` and `SagaReaderGrain`, and
`Sagas/SagaIsolationTests`, which covers the saga's retirement.

### D2 — The silo's projection names are worked out once, from the same call the starter uses

A singleton `ProjectionRegistrations`, registered alongside the projection grains, holds the set of names. It is
built lazily on first use: it opens a scope, resolves `IProjectionHandler` and every `IProjection`, and maps them
through `GetProjectionName`. This is exactly the computation `ProjectionNudgeTarget` does. A silo asks it at most
once, however many readers it activates.

*Alternative rejected:* reading the projection types from the service descriptors, as `SagaRegistrations` does.
That gives types, not names, and would bypass a host's own `IProjectionHandler` that names projections
differently. The reader would then retire for a projection it should run, or run for one it should not.

*Alternative rejected:* resolving every projection in each activation. It gives the same answer at a cost of
16 × N constructions on a cold start, for no gain.

*Construction without a session is not a new exposure.* `ProjectionNudgeTarget` already constructs every
projection without an entry's session, at silo start and on every wake-up. A projection that cannot be constructed
that way already fails the host there. The registry therefore adds no failure mode, and the request's concern about
constructing projections outside a session does not apply.

`ResolveProjection` keeps its throw as a guard. With D1 in place it cannot be reached for an unregistered name.

*Evidence:* `ProjectionNudgeTarget`, `StoreReaderGrainStarter` and `StoreWakeUpGrain` in the implementation.

### D3 — A new log event, `117_136 UnregisteredProjectionReaderRetired` (Information)

The event carries the projection's name and the partition: *"The reader of projection {Projection} on partition
{Partition} retired: no projection of that name is registered on this silo; its keep-alive is unregistered."* It is
added to `LogEvents.Orleans`, to `OrleansLog`, and to `docs/reference/log-events-schema.md`.

*Alternative rejected:* reusing `117_126 UnregisteredSagaReaderRetired`. Its name, its message and its `Saga` field
say saga, and an operator may already filter or alert on it. Reusing `117_005 StoreReaderRetired` was rejected for
the same reason: it carries a partition count that means nothing here.

### D4 — Checkpoints stay; the guide says how to remove them

A retired reader writes nothing to the checkpoint store.

- In a rolling deployment that removes a projection, a silo of the new version can activate a reader whose old
  silo is still advancing that checkpoint. Deleting it there would make the projection apply the store again from
  the beginning on the silo that still runs it.
- A projection registered again resumes from its checkpoint. A saga does the same, and the spec scenario relies
  on it.
- The reset already refuses to touch a consumer the host does not register (`orleans-execution`, *A host can reset
  what the execution model keeps outside the event stream*). An automatic delete would contradict it.

The operating guide says to delete the consumer's rows from `projection_checkpoint`, where `projection` is the
consumer's name, once no silo registers the consumer. The table is already documented in
`docs/guides/migrate-to-the-orleans-execution-model.md`.

*Evidence:* the reset requirement and the saga guide text (`operate-the-orleans-execution-model.md`, which says the
saga's old checkpoints stay until a host-owned delete).

### D5 — `Unregistered`, not `Superseded`

The reader deactivates at once, as the saga reader does, so that a silo that registers the projection can host the
next activation. `Superseded` would leave the activation to idle collection. That is right only for a key no silo
of any version runs.

### D6 — A retired reader deactivates after it answers the call that activated it

Found while implementing: the first green run of `UnregisteredProjectionReaderTests` logged the retirement three
times per partition. The silo log showed why. `DeactivateOnIdle` inside `OnActivateAsync` cancels the activation, so
Orleans forwards the message that activated it — the keep-alive tick — to a new activation. That one retires again,
and so does the next, until `MaxForwardCount` (2) is reached and the runtime rejects the tick: `Could not deliver
reminder tick` at Error. The same code path served the saga reader's retirement and the retirement beyond a lowered
partition count, whose tests checked only that a retirement was logged.

The base grain now records on activation that a retired reader deactivates, and does so in the first call it
answers (`AnswersAsRetired`, which every entry point uses in place of `Retired`). `DeactivateOnIdle` called during a
request deactivates the activation once the request completes, so the activating call is answered and nothing is
forwarded. A superseded reader is unchanged: it still waits for idle collection.

*Alternative rejected:* a grain timer with no delay that deactivates. Whether it runs before or after the queued
activating message is not guaranteed, and if it runs first the message is forwarded again.

*Evidence:* `UnregisteredProjectionReaderTests` (exactly one retirement per partition, no undelivered tick), and
the assertions added to `RetiredReaderTests` and `SagaIsolationTests`. Both failed against the old base grain with
three retirements and passed with the change.

## Risks / Trade-offs

- **[Rolling deployment that adds a projection]** A silo of the earlier version can be chosen for the new
  projection's reader. That happens through a wake-up after a commit on a new silo, or through the new silo's start
  call. The reader retires there and unregisters the keep-alive that the new silo registered. The reader still
  reads on every wake-up and polls while it is active, so no fact is lost. Only the keep-alive safety net is
  missing, until the next start of a silo of the new version registers it again. Saga readers have behaved this way
  since 4.2.0. → The guide says so in the passage on retirement, and recommends a restart of one silo after such a
  deployment where the keep-alive matters. Keeping readers off such silos would need a placement that knows each
  silo's registrations, which is out of scope.
- **[A host's `IProjectionHandler` names projections non-deterministically]** The registry caches the first
  answer. → The names already key checkpoints and reader grains. A handler whose names change already breaks both,
  so the cache adds nothing new.
- **[Registry built before the container is complete]** → It is built lazily, on the first activation that asks.
  That always happens after the silo is active.

## Migration Plan

None. A host upgrades by moving to 4.4.9. On the first start, the readers of a projection it no longer registers
retire and their reminders are gone. A downgrade brings back the old behaviour only for reminders that something
registers again, which only a host that registers the projection does.
