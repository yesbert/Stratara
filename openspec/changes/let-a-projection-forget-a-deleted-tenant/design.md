## Context

See `proposal.md`, section Why. The relevant code, as of 4.3.1:

- `ProjectionHandler` (`src/Stratara.Projections/Services/ProjectionHandler.cs`, internal, scoped) is
  the default `IProjectionHandler`. It applies one projection's relevant events in order, one at a
  time. Every execution path goes through it: the bus worker through `ProjectionManager`, the replay
  worker through `ProjectionManager`, and the Orleans `ProjectionGrain` directly. The grain caches
  `GetRelevantEventTypeNames` per grain.
- `ProjectionManager` hands a projection only the events whose type names are in
  `GetRelevantEventTypeNames`, which the method invoker derives from the projection's `HandleAsync`
  overloads. An event with no overload gets the invoker's no-op delegate.
- `PrecedingFactMissingException` propagates out of `ProjectAsync`. The bus worker then retries under
  the `PrecedingFact` policy, and the replay under the replay-batch policy.
- `ProjectionReplayWorker` is registered by every projection host (`AddStrataraProjections`). Its
  `RunReplayAsync` calls `IProjectionViewTruncator.TruncateAllAsync` before it replays, and the Orleans
  model wraps that truncator to reset checkpoints around it (`ReplayCheckpointResetTruncator`). One
  place therefore empties the read models for a full replay on both models.
- `ProjectionRebuilder` (`src/Stratara.Orleans/Projections/ProjectionRebuilder.cs`) rebuilds one
  `IRebuildableProjection` by calling its `TruncateAsync` inside `TruncationBetweenResets.RunAsync`.
- The framework's read context `ReadDbContext<TContext>` declares every configuration in the
  `…ReadStore` namespace. The checkpoint table (`projection_checkpoint`) was added this way, with its
  store in `Stratara.Orleans.EntityFrameworkCore` and an explicit `AddStrataraProjectionCheckpoints<T>()`
  registration.
- `AddProjectionsFromAssemblyContaining<T>` trusts the payload type of every `HandleAsync` it finds.
- `TenantDeleted` carries only a timestamp and is appended to the tenant's own stream, so the tenant
  is the fact's stream id. `CustomerTenantsDeleted` lists the tenant ids in its payload.
- `IEvent.TenantId` is the fact's owning tenant: the subject, anchored to the stream since 4.0.0.

## Goals / Non-Goals

**Goals:**

- A declaring projection survives, live and in a replay, a fact recorded after the deletion of its
  tenant.
- Every other projection, and every other outcome of a declaring projection, is unchanged.
- The record is exact per projection on both execution models, including store-reading projections
  that run at different paces.

**Non-Goals:**

- **Removing a deleted tenant's data from a projection.** That stays the projection's job, as the
  tenant directory's statement of erasure coverage says. The framework only remembers that it
  happened.
- **Suppressing facts of a deleted tenant that apply cleanly**, such as a late creation that would
  insert a row. Only the outcome of a missing-prerequisite report changes. A projection that wants to
  drop those facts can ask the store itself, because the store contract is public.
- **A consumer's own deletion facts.** Only the tenant-deletion facts the framework ships are
  recognised. Letting a projection name its own is a larger surface. It can be added later without
  changing this one.
- **Sagas.** A saga reports a missing prerequisite the same way. No consumer has hit the case there,
  and a saga's state is per correlation, not per tenant.
- **Backfilling the record for deletions applied before the upgrade** (see Migration Plan).

## Decisions

### The declaration is a marker interface on the projection

`IForgetsDeletedTenants : IProjection`, with no members, in `Stratara.Projections.Abstractions`,
beside `IRebuildableProjection`.

*Alternative rejected: a registration call per projection.* The declaration describes the projection,
as `IRebuildableProjection` does, and discovery already scans for projection types.

*Alternative rejected: always on.* Every projection host would need the store, and so the table and
its registration. Outcomes would also change for projections that never asked for it.

**The declaration is a promise.** Once either deletion fact has been applied, the tenant's data is gone
from the projection's read model, whether its own handlers remove it or something else does. The
framework records both facts for a declaring projection, so a projection that keeps a tenant's rows
after `TenantDeleted`, the tenant's soft delete, must not declare it. Otherwise a genuine "not yet" for
that tenant would be passed over for good. An independent review found that the guide's first example
broke this promise.

*Alternative rejected: record only the deletion facts the projection handles itself.* It would keep a
projection from promising too much, but it fails a projection whose rows go by other means. An example
is a foreign key that cascades from a table another projection keeps. That is the case the reporting
consumer's decorator covered by recording both facts for every declaring projection.

### The behaviour lives in the default projection handler

`ProjectionHandler` does four things for a declaring projection:

1. Adds `TenantDeleted` and `CustomerTenantsDeleted` to its relevant event types, without duplicates.
   The names follow, so the manager and the Orleans grain hand them over.
2. Applies each event as today. A deletion event with no overload runs the no-op delegate.
3. After a deletion event has been applied, records the tenants it deleted, before the next event. A
   later fact in the same bundle therefore sees the record. `TenantDeleted` records its stream id.
   `CustomerTenantsDeleted` records its `TenantIds`, and an empty or missing list records nothing.
4. Catches `PrecedingFactMissingException` around each event. If the store says this projection has
   forgotten the event's `TenantId`, it logs the pass-over and continues with the next event.
   Otherwise it rethrows with `throw;`.

*Alternative rejected: a decorator of `IProjectionHandler`, which is what the reporting consumer
wrote.* The handler is internal and scoped, and inline code is simpler. A host that replaces
`IProjectionHandler` wholesale gives up the behaviour, and the documentation says so.

*Alternative rejected: record the deletion in the store transaction of the projection's own handler.*
The framework has no transaction that spans a handler. Handlers open their own. The record is written
after the handler returns. A crash in between leaves the fact unacknowledged, and redelivery or the
batch retry applies it again. Handlers are idempotent by requirement, and recording is too.

Evidence: the consumer's decorator, which passes over exactly the missing-prerequisite report for a
recorded tenant, and the evidence in the report: the replay failed at a fact at sequence 1065, after
the cascade at 1063 and the stream's creation at 1055.

### The store contract is public, and the read store implements it

`IForgottenTenantStore` in `Stratara.Projections.Abstractions`:

- `ForgetAsync(projection, tenantIds)`: idempotent.
- `HasForgottenAsync(projection, tenantId)`.
- `ClearAsync(projection)`.

A first version also had `ClearAllAsync()`, for the replay. The review showed that it would empty
another deployment's records in a shared read store, so it is gone (see Clearing).

`ForgottenTenantStore<TContext>` in `Stratara.EventSourcing.EntityFrameworkCore`, namespace
`…ReadStore.ForgottenTenants`, implements it. It uses an `IDbContextFactory<TContext>` and one context
per call, as `ProjectionCheckpointStore` does:

- Entity `ForgottenTenant(Projection, TenantId)`, table `projection_forgotten_tenant`, primary key on
  both columns, projection name up to 255 characters.
- `ForgetAsync` adds the missing rows. When a concurrent writer inserts some of them first, the save
  fails as a whole. It then clears the tracker and inserts the rows still missing, up to three
  attempts in all. This covers two partitions of one projection recording overlapping tenants at once.
  A conflict that outlasts the attempts propagates, and the fact is applied again.
- `ClearAsync` uses `ExecuteDeleteAsync`.

Because the configuration sits in the `ReadStore` namespace, every context derived from
`ReadDbContext<T>` declares the table.

`AddNpgsqlReadDbContextFactory<TDbContext>` registers the store with `TryAddScoped`. A new
`AddStrataraForgottenTenants<TReadContext>()` registers it for a host whose read context is registered
another way. The test hosts use it too.

*Alternative rejected: derive the record from the framework's own tenant read model.* It would need
tombstones there, which changes what `ITenantRepository` returns and what the tenant view retains
after an erasure. It would also make every projection depend on how far the tenant projection has got,
which is the race the per-projection record exists to avoid on the store-reading model.

*Alternative rejected: keep the record in memory.* A late fact can arrive after a restart.

### The projection name is the handler's name for it

The record is keyed by `IProjectionHandler.GetProjectionName`, the projection type's name. The
checkpoints and the rebuild API use the same key, so a rebuild clears exactly what it should.

### A missing store fails on the first fact

`ProjectionHandler` takes the store as an optional constructor parameter, as `CommandOutboxDispatcher`
takes its resolver. A declaring projection handed any fact when the store is absent throws
`InvalidOperationException`, which names both registrations. It fails and does not skip, because a
projection that declared the behaviour and silently lacked it would fail later and more confusingly.

### A store that fails keeps the missing prerequisite

If `HasForgottenAsync` throws while the handler handles a `PrecedingFactMissingException`, the handler
throws a new `PrecedingFactMissingException` for the same stream and event type, with the store's
failure as its inner exception. The bundle therefore keeps the missing-prerequisite retry. It does not
turn into an ordinary failure that fails on its first attempt. Cancellation passes through unchanged.

### Clearing: only the host's declaring projections, and before the read models

- `ProjectionReplayWorker.RunReplayAsync`: before `TruncateAllAsync`, it resolves the registered
  `IProjection`s from the truncation scope. For each that declares the marker, it calls
  `ClearAsync(name)`. A host without a declaring projection does not resolve the store at all.
- `ProjectionRebuilder`: for a declaring projection, the truncation it hands to
  `TruncationBetweenResets.RunAsync` becomes `ClearAsync(projectionName)` followed by the projection's
  `TruncateAsync`. Both therefore run between the two checkpoint resets.

The first version cleared everything after the truncation. The review found three faults in that:

1. **Shared read stores.** Deployments may share a read store under distinct projection names, as the
   checkpoints already allow. Clearing everything emptied another deployment's records while its read
   models kept the deletions. Its next late fact would then dead-letter, or stall a partition for good.
   Clearing by name, for the host's own declaring projections, is how the checkpoint reset already
   behaves.
2. **Failure after the damage.** In a host without a declaring projection whose read database lacks
   the table, the clear failed after the truncation and left the read models empty. Now such a host
   never touches the store. A declaring host that lacks the table fails before anything is emptied.
3. **Order.** On the Orleans model the replay's clear ran after the readers had resumed. A reader
   whose replay flag had lapsed could record a deletion, checkpoint past it, and then have the record
   erased. A later late fact would then stall its partition.

   Clearing before the truncation reverses the risk. A deletion applied in between is recorded again
   when it is re-applied, and a record that outlives the reset only concerns a tenant whose data the
   projection removes at the deletion anyway. By the promise above, passing over that tenant's facts
   earlier cannot change the rebuilt state.

### Discovery trusts the deletion facts for a declaring projection

`AddProjectionsFromAssemblyContaining<T>` registers `TenantDeleted` and `CustomerTenantsDeleted` for a
type that implements the marker, so a host whose projections do not handle them can still read them.
A projection registered by hand needs `AddTrustedType<T>()` for both, as for any payload, and the
documentation says so.

### One log event

`LogEvents.ProjectionForgottenTenantFactPassedOver = 104_014`, at Information, source-generated in
`src/Stratara.Projections/Diagnostics/Extensions`. It names the projection, the stream, the event type
and the tenant. It is Information rather than Debug because an operator looking for why a fact had no
effect must be able to find it. It is not a Warning, because nothing is wrong.

## Risks / Trade-offs

- [Every consumer must add a migration, including those that never declare a projection.] → The
  upgrade note says so. The checkpoint table set the precedent. A conditional model would need the
  context to know about DI registrations.
- [After the upgrade, deletions applied before it are not in the record.] → A replay records them from
  the history, and so does rebuilding the projection on its own on the Orleans model for an
  `IRebuildableProjection`. The upgrade note recommends a replay before relying on the behaviour for
  tenants deleted earlier.
- [A projection declares the marker but keeps a tenant's rows after one of the deletion facts.] → The
  promise is stated on the marker, in the requirement and in the guide, whose example handles both
  facts.
- [A genuine ordering problem for a deleted tenant's fact is passed over.] → The tenant's data is gone
  by design, and the pass-over is logged.
- [A fact of a tenant is handled before the projection has applied the tenant's deletion, live, on
  another consumer.] → It finds its row, or it is retried under the existing policy until the deletion
  is applied and recorded. Either way the end state is the deleted one.
- [A host replaces `IProjectionHandler`.] → The behaviour is absent. The documentation names this, and
  the declaring projection does not fail on its own. Accepted, because replacing the handler already
  opts out of the handler's other guarantees.

## Migration Plan

1. Upgrade, then generate an EF Core migration for the read context. It adds
   `projection_forgotten_tenant`.
2. Declare `IForgetsDeletedTenants` on the projections that remove a deleted tenant's rows.
3. Run a replay so the record covers deletions applied before the upgrade. On the Orleans model, an
   `IRebuildableProjection` can be rebuilt on its own instead.

A consumer that kept its own record in a decorator can remove the decorator after step 3.

Rollback: remove the declarations. The table can stay.
