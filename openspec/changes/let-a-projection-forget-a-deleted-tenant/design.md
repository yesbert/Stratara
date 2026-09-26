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
- `ClearAllAsync()`.

`ForgottenTenantStore<TContext>` in `Stratara.EventSourcing.EntityFrameworkCore`, namespace
`…ReadStore.ForgottenTenants`, implements it. It uses an `IDbContextFactory<TContext>` and one context
per call, as `ProjectionCheckpointStore` does:

- Entity `ForgottenTenant(Projection, TenantId)`, table `projection_forgotten_tenant`, primary key on
  both columns, projection name up to 255 characters.
- `ForgetAsync` adds the missing rows. When a concurrent writer wins, it clears the tracker and
  re-checks, as the checkpoint store's `CreateAsync` does.
- The two clears use `ExecuteDeleteAsync`.

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

### Clearing happens where the read models are emptied

- `ProjectionReplayWorker.RunReplayAsync`: right after `TruncateAllAsync`, it resolves the store from
  the same scope, if one is registered, and calls `ClearAllAsync`. A failure fails the replay like a
  truncation failure.
- `ProjectionRebuilder`: the truncation it hands to `TruncationBetweenResets.RunAsync` becomes the
  projection's `TruncateAsync` followed by `ClearAsync(projectionName)`. Both therefore run between the
  two checkpoint resets.

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
- [After the upgrade, deletions applied before it are not in the record.] → A replay, or a rebuild of
  the projection, records them from the history. The upgrade note recommends a replay before relying
  on the behaviour for tenants deleted earlier.
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
3. Run a replay, or rebuild those projections, so the record covers deletions applied before the
   upgrade.

A consumer that kept its own record in a decorator can remove the decorator after step 3.

Rollback: remove the declarations. The table can stay.
