# Let a host wait for its own write

> **Status:** approved

## Why

`IProjectionProgress.MarkAsync()` (4.4.6) returns a mark of everything committed, in every partition. A host that wants
to read its own write takes that mark right after the write and waits, so it waits until the projection has caught up
with the whole store, not only with the fact it wrote. Under steady write traffic elsewhere the wait grows with that
traffic. A consumer reported it on 2026-10-23 (consumer finding SF-011): its "set a connection" waits for the
connections projection to pass entries of other aggregates in partitions its write never touched, and at its bound it
answers without the guarantee. The narrower mark was named in `let-a-host-wait-for-a-projection` as deferred.

## What Changes

The consumer-visible effect is that a host can take a mark that covers what it just wrote and wait only for the
partitions that write touched.

- **New overload `IProjectionProgress.MarkAsync(IEnumerable<Guid> streamIds, CancellationToken)`.** Taken after a
  commit, it covers every entry committed to those streams by then, and therefore the caller's own commit. It
  constrains only the partitions those streams fall into. The default interface implementation answers the global
  mark, which is wider but still correct, so an implementation of the interface outside the framework keeps
  compiling and keeps its promise.
- **`WaitAsync` is unchanged.** A partition the mark does not constrain is reached at once, as a partition with
  nothing committed already is. The answers are those of 4.4.6.
- **Documentation:** the projection guide's *Read your own write* takes the stream mark, and says when the global
  mark is the right one: a decision that needs everything committed, such as a refusal across tenants.

Unchanged: the global mark, the wait, its answers and its handling of rebuilds and replays.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: gains *A host can wait until a projection has applied its own write*.

## Impact

- `src/Stratara.Abstractions/Abstractions/Projections/IProjectionProgress.cs`: the new overload, with a default.
- `src/Stratara.Orleans/Projections/ProjectionProgress.cs`: the stream mark, using the committed head of each
  touched partition and 0 elsewhere.
- `docs/guides/write-a-projection.md`, `docs/guides/operate-the-orleans-execution-model.md`, `llms.txt`,
  `CHANGELOG.md`.
- Additive: one interface member with a default. No schema change, no migration. A patch release (4.4.7).
- Dissolved or superseded legacy sources: none.
