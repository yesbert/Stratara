# let-the-read-side-be-reset-where-it-runs

> **Status:** approved (owner, 2026-09-17 — recorded at the owner's request)

## Why

The review that replaced Copilot's on the pull requests merged on 2026-09-17 followed the read side's
recovery paths and found three of them broken or unreachable.

- **The checkpoint guard blocks the reset its own message asks for.** Since
  `keep-a-store-reader-batch-tenant-correct-and-its-checkpoint-guarded` a checkpoint held under
  another reader name is refused — and the reader name carries the partition count. After a lowered
  partition count every surviving checkpoint is refused, and the two verbs that recover from it,
  a projection's rebuild and the full replay's reset, write the beginning *under the new name* and
  are refused too. What the message tells the operator to do ("reset the checkpoint before switching")
  can then only be done with the whole deployment stopped.
- **A pause that fails leaves partitions paused for good.** The rebuild and the replay reset pause
  every partition before their `try`, so a pause that throws — a partition whose batch outlives the
  response timeout is enough — leaves the partitions that did pause with a pauser that is never
  released. Since rebuilds began counting their pausers, a later rebuild no longer clears it: the
  partition stops reading until the silo restarts, and nothing says so.
- **`hybrid: true` is silently ignored on a later registration.** The replacement of the bundle
  dispatcher is skipped once the model's dispatcher is registered, so a host that registers the
  projection role first and then the saga role with `hybrid: true` keeps neither the bus dispatcher
  nor the failure the registration promises. Bundles stop reaching the bus and nothing says so.
- **A wake-up that cannot be sent is swallowed whole.** The dispatcher catches every nudge failure
  without a log, and the nudge target stops at the first failure, so the remaining consumers of that
  commit lose their wake-up too. A permanently broken wake-up path shows only as poll-interval
  latency.

## What Changes

- **A reset is accepted whatever reader wrote the row.** The checkpoint store gains a reset of its
  own — the beginning, under the resetting host's reader name — and the rebuild and the replay use
  it. The guard on reading and on advancing is unchanged.
- **The readers are paused one by one and only what paused is resumed.** A pause that fails resumes
  what it had paused and fails naming the partition.
- **A registration that asks to keep the bus is answered.** Whether it is the first store-reading
  role or a later one, the bus dispatcher is kept or the registration fails naming what is missing.
  The kept dispatcher is registered by its own type where it has one, so the container disposes it.
- **A wake-up that fails is logged and does not stop the others.** New log event `117_121`, and the
  nudge target goes on to the consumers behind the one that failed.

## Impact

- Affected specs: `orleans-execution`
- Affected code: `src/Stratara.Abstractions/Projections/IProjectionCheckpointStore.cs`,
  `src/Stratara.Orleans.EntityFrameworkCore/Projections/ProjectionCheckpointStore.cs`,
  `src/Stratara.Orleans/Projections/ProjectionRebuilder.cs`,
  `src/Stratara.Orleans/Projections/ReplayCheckpointReset.cs`,
  `src/Stratara.Orleans/Projections/StoreReaderPause.cs` (new),
  `src/Stratara.Orleans/Projections/OrleansEventBundleDispatcher.cs`,
  `src/Stratara.Orleans/Projections/ProjectionGrain.cs`,
  `src/Stratara.Orleans/DependencyInjection/OrleansProjectionServiceCollectionExtensions.cs`,
  `src/Stratara.Diagnostics/LogEvents.cs`
- Affected docs: `docs/guides/operate-the-orleans-execution-model.md`,
  `docs/reference/log-events-schema.md`, `CHANGELOG.md`
- Public API: `IProjectionCheckpointStore.ResetAsync` is added with a default implementation, so an
  implementation outside the framework keeps compiling.
