# Let a projection forget a deleted tenant

> **Status:** approved

## Why

A consumer deleted a customer and, with it, the customer's tenants, using the cascade event the tenant
package ships. Its projections followed the deletion and removed the tenants' rows, as a consumer
must: the framework states that read models a consumer's projections built are the consumer's to
clear. Work that had been queued for those tenants before the deletion then ran to its end and
recorded its facts. That is legitimate history: indexing finished for a knowledge entry that no
longer existed on the read side. The projection that met such a fact found no row and reported a
missing prerequisite, as the framework asks it to.

From there the framework has only two outcomes, and both are wrong here. The live worker retries the
bundle for a few seconds, then fails it, and the transport dead-letters it. A replay retries the
batch and fails, on every attempt. Because a replay empties the read models first, every attempt
leaves them empty or partial. One such fact is enough to make every later replay impossible. The
consumer that reported this found facts like it for eleven entries and two integrations.

The missing-prerequisite report distinguishes "the beginning has not arrived yet" from "the beginning
will never arrive", and only time tells those apart. A deleted tenant is a third case: the beginning
arrived, and the projection removed it on purpose. The framework ships the deletion facts. It knows
which tenant owns every fact. It does not remember which tenants a projection has seen deleted, so it
cannot tell the third case from the second. The consumer worked around this with a decorator that
keeps that memory per projection. That decorator depends on nothing but framework types, which is
the sign the memory belongs here.

## What Changes

- A projection can declare that it forgets a deleted tenant. The declaration is a marker interface on
  the projection. It is a promise: once either deletion fact is applied, the tenant's data is gone
  from the projection's read model.
- For such a projection the framework hands it the tenant-deletion facts it ships: a tenant's
  deletion, and the cascade that deletes all of a customer's tenants. It does so whether or not the
  projection handles them itself. After the projection has applied one, the framework records, for
  that projection, the tenants it deleted. The record is kept in the order that projection applies
  facts, so projections that run in parallel, or each at its own pace from the store, cannot race it.
- When such a projection reports a missing prerequisite for a fact whose owning tenant it has
  recorded as deleted, the fact is **passed over**. It is treated as applied, it is not retried, and
  it is logged with the projection, the stream, the fact's type and the tenant. Every other outcome
  is unchanged:
  - a fact of a deleted tenant that the projection applies without complaint is applied;
  - a missing prerequisite for any other tenant is retried and fails as before;
  - a projection that does not declare itself is not affected at all.
- The record is derived data. A full replay empties the record of each declaring projection it
  registers, before it empties the read models, and leaves every other deployment's records in a
  shared read store alone. Rebuilding one projection on its own empties that projection's record just
  before its read model. A replay therefore rebuilds it from the history, in order. A host without a
  declaring projection never touches the record's table.
- The record lives in the read store, in a new table the framework's read context declares.
  **Upgrading requires a migration** of the consumer's read context, as the checkpoint table did. The
  store is registered by `AddNpgsqlReadDbContextFactory<TContext>()`. A host that registers its read
  context another way registers it with a new call. A projection that declares itself in a host with
  no store fails on the first fact it is handed, naming the registration, and does not silently skip
  the record.
- The test support that hosts projections in the test's process registers the store as well.

A host with no projection that declares itself sees no difference beyond the migration.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `projections`:
  - A new requirement, *A projection can forget a deleted tenant*. It covers the declaration and its
    promise, the dispatch of the deletion facts, the per-projection record and when it is cleared, the
    pass-over of a missing prerequisite, and the failure when no store is registered.
  - *A projection declares the events it cares about by handling them* gains the exception for the
    deletion facts.
  - *A projection can report that a fact's prerequisite has not been applied yet* gains the exception
    for a recorded tenant.

## Impact

- `src/Stratara.Projections/Abstractions/`: the marker interface and the store contract, both public.
- `src/Stratara.Projections/Services/ProjectionHandler.cs`: the deletion facts count as relevant to a
  declaring projection, the record is written after a deletion fact is applied, and a missing
  prerequisite is passed over for a recorded tenant.
  `src/Stratara.Projections/Services/ProjectionReplayWorker.cs`: a full replay clears the record.
  `src/Stratara.Projections/DependencyInjection/ProjectionServiceCollectionExtensions.cs`: discovery
  trusts the deletion facts for a declaring projection.
- `src/Stratara.EventSourcing.EntityFrameworkCore/ReadStore/`: the table, its entity and the store.
  `AddNpgsqlReadDbContextFactory` registers the store, and a new registration call serves other read
  contexts.
- `src/Stratara.Orleans/Projections/ProjectionRebuilder.cs`: rebuilding one projection clears its
  record. `src/Stratara.Testing.Orleans/ExecutionModelTestHost.cs` registers the store.
- `src/Stratara.Diagnostics/LogEvents.cs`: one new event id for a passed-over fact.
- Tests in `tests/Stratara.Projections.Tests`, `tests/Stratara.EntityFrameworkCore.Tests`,
  `tests/Stratara.Orleans.Tests`, `tests/Stratara.Testing.Orleans.Tests`.
- `llms-full.txt`: the regenerated reference catalogue.
- `docs/guides/write-a-projection.md`, `docs/guides/tenant-membership.md`,
  `docs/reference/di-extensions-cheatsheet.md`, `src/Stratara.Projections/README.md`.
- `CHANGELOG.md`: a minor release, because of the new public surface and the new table.
- Nothing is dissolved or superseded by this change.
