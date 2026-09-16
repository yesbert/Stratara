# Scope the reset to the host's consumers

> **Status:** proposed

## Why

The execution-model reset clears reminders and membership only for the host's own service and
cluster, but deletes every checkpoint in the read store. A second deployment or service that keeps its
store-reading projections in the same read database loses its checkpoints to a reset it never ran, and
rebuilds those projections from the stream without anyone having asked for it. The requirement says
"no checkpoint remains" and does not say whose, so the code keeps the letter of a promise that is
wider than an operator expects. Found reviewing #96.

Investigating it showed why the scope was never stated: a checkpoint is identified by its consumer
and partition, not by a deployment, and every deployment's store-reading sagas share one consumer. A
read store shared by two deployments that both run sagas already has them overwrite each other's
position in normal operation. What a read store may be shared for is unstated, and the reset is only
the loudest place where that shows.

## What Changes

- The reset removes the checkpoints of the store-reading projections and sagas the host registers,
  and no other checkpoint in the read store. Reminders, membership and the directory are cleared as
  today; the event stream is still never touched.
- The report counts the checkpoints removed, as today — now only the host's own.
- The specification states what a read store may be shared for: the checkpoints in it are keyed by
  consumer, so two deployments may share a read store only when their consumers' names differ, and
  at most one of them runs store-reading sagas.
- The operations guide says the reset must run from a composition that registers the host's
  projections and sagas, and that a checkpoint of a consumer the host no longer registers is left in
  place and never read.
- **Behaviour change for a host that relied on the reset clearing checkpoints it does not register**
  (for example, a projection removed since): those rows now stay. They are inert — nothing reads a
  checkpoint whose consumer is not registered — and removing them is a plain delete the host owns.
- No schema change, no migration, no new public type or member.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: *A host can reset what the execution model keeps outside the event stream* —
  the checkpoints cleared are the host's registered consumers' only; a new scenario covers a read
  store shared with another deployment. A new requirement states when a read store may be shared.

## Impact

- `Stratara.Orleans.EntityFrameworkCore` — the reset's checkpoint deletion is filtered to the host's
  consumer names.
- `Stratara.Orleans` — makes the names of the host's registered store-reading consumers available to
  the reset implementation, without adding public surface.
- `docs/guides/operate-the-orleans-execution-model.md` → *Reset what the model keeps*; the XML
  documentation of `IExecutionModelReset`.
- Tests: the reset integration test gains a checkpoint of an unregistered consumer that must survive.
- Versioning: a behaviour change without API change, shipped as a patch.
- Not in scope: a deployment discriminator on the checkpoint row (schema change, owner decision
  2026-09-16); detecting two deployments that write the same consumer's checkpoints.
