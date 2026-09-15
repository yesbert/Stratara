## MODIFIED Requirements

### Requirement: A replay truncates every read model before rebuilding

A replay SHALL mark itself active, empty every registered read model, then replay the whole event
stream from the beginning in batches, and mark itself inactive when it finishes — whether it
succeeded or not.

The active marking SHALL be held for a bounded period that the replaying host renews while it works,
so that a replay whose host stops without marking itself inactive ceases to be marked active without
operator intervention. The period SHALL be configurable and SHALL default to a value that outlasts a
slow batch, because a marking that lapses while its replay is still running would let suppressed
publication resume mid-rebuild.

Truncation is what makes a replay a rebuild rather than a re-application: without it, events would
be applied a second time on top of state that already reflects them.

On the Orleans execution model a projection that declares how to empty its own read model MAY be
rebuilt alone: its checkpoints are reset, its read model emptied, and its partitions re-read from the
beginning in parallel, while every other projection keeps applying live events; a rebuild that fails
part-way resumes from the beginning. A projection that does not declare it is rebuilt with the others
by the replay, as before, and on a host whose projections read the store a full replay SHALL also
return their checkpoints to the beginning, so that they re-read what the replay emptied.

#### Scenario: A replay runs to completion

- **WHEN** a replay runs over a non-empty stream
- **THEN** it activates, truncates every read model, replays the stream in batches, records how many
  events it replayed, and deactivates

#### Scenario: The stream is empty

- **WHEN** a replay runs over an empty stream
- **THEN** it still truncates the read models and still deactivates — a rebuild from nothing produces
  nothing, not the previous contents

#### Scenario: A replay fails partway

- **WHEN** a replay fails after truncating
- **THEN** it deactivates regardless, and the read models are left in whatever partial state the
  replay reached

#### Scenario: A replay's host stops without deactivating

- **WHEN** the host running a replay stops without the replay marking itself inactive
- **THEN** the active marking lapses once it is no longer renewed, and publication is no longer
  suppressed

#### Scenario: A replay is still working

- **WHEN** a replay is between batches and has not finished
- **THEN** the active marking is renewed, so it does not lapse while the replay is still running

#### Scenario: One projection is rebuilt on the Orleans execution model

- **WHEN** a rebuildable projection is asked to rebuild while others are registered
- **THEN** only its read model is emptied and re-read, and the others apply live events throughout —
  verified with a hundred thousand events over three projections on the PostgreSQL store

#### Scenario: A full replay runs on a host with store-reading projections

- **WHEN** a full replay runs while projections read the store from checkpoints
- **THEN** their read models are emptied with the others and refilled from the beginning of the store,
  and no checkpoint is left past an entry whose effect the replay removed

### Requirement: Bundles about one aggregate are applied one at a time within a process

Where the projection worker processes bundles in parallel, it SHALL ensure that two bundles whose
events belong to the same aggregate stream are not applied concurrently within one process, while
bundles about different aggregates continue to be applied in parallel.

This is the same guarantee the command side gives for commands naming one aggregate. Without it, a
follow-up fact handed to one consumer can be applied before the fact that created the entity, which
is still in flight on another. The guarantee is per process: two processes consuming the same
subscription do not serialise against each other.

On the Orleans execution model the guarantee holds across the cluster: one reader per projection
and partition applies one batch at a time, so two facts about one aggregate never apply
concurrently anywhere.

#### Scenario: Two bundles about the same aggregate arrive concurrently

- **WHEN** two bundles whose events belong to one aggregate stream are handed to two parallel
  consumers of the same process at once
- **THEN** the second is applied only after the first has completed

#### Scenario: Two bundles about different aggregates arrive concurrently

- **WHEN** two bundles whose events belong to different aggregate streams are handed to two parallel
  consumers of the same process at once
- **THEN** both are applied in parallel

#### Scenario: A bundle spans more than one aggregate

- **WHEN** a bundle carries events from more than one aggregate stream
- **THEN** it is applied concurrently with no other bundle about any of those streams, and two such
  bundles cannot wait on each other indefinitely

#### Scenario: The number of aggregates exceeds the number of locks

- **WHEN** more distinct aggregates are in flight than the framework holds locks for
- **THEN** correctness is preserved — two unrelated aggregates may serialise against each other, but
  two bundles about the same aggregate never apply concurrently

#### Scenario: Two hosts apply facts about one aggregate on the Orleans execution model

- **WHEN** two silos run the projection readers and facts about one aggregate are committed
- **THEN** they are applied by one reader, one batch at a time, never concurrently

## ADDED Requirements

### Requirement: A projection can read the store from a checkpoint instead of consuming the bus

On the Orleans execution model a projection SHALL be fed from the event store in commit order from
a checkpoint per partition, under the session recorded with each entry, with a commit as the
wake-up and a poll as the safety net; the bus SHALL be optional for projections on that model, and
a host MAY keep publishing bundles so that both paths run — whichever applies a fact first wins,
idempotently. Every guarantee of this capability that speaks of a bundle SHALL hold for an entry
read from the store.

#### Scenario: A fact is committed

- **WHEN** events are committed on a host with store-reading projections
- **THEN** the projections apply them after the wake-up, within the latency the push path gives —
  verified at two thousand events per run on the PostgreSQL store

#### Scenario: The wake-up is lost

- **WHEN** a commit's wake-up never reaches a reader
- **THEN** the poll applies the events at its next interval, and nothing is lost

### Requirement: A stalled partition is reported

Where a store-reading projection cannot apply an entry and the checkpoint of that partition stops
advancing, the framework SHALL log the entry it stopped at and count the stall, so that an operator
sees a partition that is not moving without inspecting checkpoints.

#### Scenario: An entry keeps failing

- **WHEN** an entry fails on every retry
- **THEN** each retry is logged with the projection, the partition and the entry's identity, and the
  stall counter for that projection and partition rises
