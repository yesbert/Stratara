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

A host SHALL be able to opt in to keeping the read models it is about to empty. Where it has, the replay
SHALL take a copy of them as one consistent state — the read models, the positions of projections that
read the store, and each projection's record of forgotten tenants, together — before it empties anything.
A replay that fails after the copy was taken SHALL write the copy back in place of what it had rebuilt,
as one step that readers observe either entirely or not at all, and SHALL record that it did. A replay
that succeeds SHALL drop the copy. A replay whose host stopped before it ended SHALL leave the copy, and
the next host with the option that starts SHALL write it back, provided no running replay owns it; when
several such hosts start at once, exactly one SHALL do so. A replay requested while such a copy exists
SHALL keep that copy rather than take a new one, because it holds the last complete state and what is
in place holds a partial one. A copy that cannot be taken, or a read model outside the copy that the
store ties to one inside it so that the copy could not be written back, SHALL fail the replay before
anything is emptied. Readers SHALL see the rebuild in progress while it runs, as without the option.

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

- **WHEN** a replay fails after truncating, on a host that did not opt in to keeping the read models
- **THEN** it deactivates regardless, and the read models are left in whatever partial state the
  replay reached

#### Scenario: A replay fails partway on a host that keeps the read models

- **WHEN** a replay fails after truncating, on a host that opted in
- **THEN** the read models, the store-reading positions and the records of forgotten tenants are
  exactly as they were before the replay began, the replay deactivates, and its outcome records the
  failure and that the read models were restored — verified on the PostgreSQL read store

#### Scenario: A replay succeeds on a host that keeps the read models

- **WHEN** a replay completes on a host that opted in
- **THEN** the read models hold the rebuilt state and no copy of the previous state remains

#### Scenario: A replay's host dies on a host that keeps the read models

- **WHEN** the host running a replay stops before the replay ends, and a host with the option starts
  afterwards while no replay is running
- **THEN** that host writes the copy back, the read models are as they were before the replay began, and
  the restoration is recorded; where several such hosts start at once, one of them writes it back

#### Scenario: A replay is requested while a copy from an unfinished replay exists

- **WHEN** a replay is requested on a host that opted in, and a copy left by a replay that did not
  finish has not yet been written back
- **THEN** the new replay keeps that copy as the state to fall back to, rather than copying the partial
  read models

#### Scenario: The copy cannot be taken

- **WHEN** the copy fails, or a read model the store ties to one inside the copy is not part of it
- **THEN** the replay fails with a message naming the cause, nothing has been emptied, and the read
  models are as they were

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

### Requirement: A replay retries a failing batch before it fails

A replay SHALL apply the event stream in batches, and where a batch fails — whether reading it from
the event store or applying it to the read models — the replay SHALL retry that batch a bounded
number of times, with backoff, before treating the failure as the replay's. A retried batch SHALL be
applied again from its first entry, and each retry SHALL be recorded so an operator watching the
replay can see it. Once the attempts are exhausted, the failure SHALL end the replay exactly as an
unretried failure does today.

The retry covers a failure that passes: a read-store timeout, a dropped connection, a lock held a
moment too long. It does not make a deterministic failure survivable, and it does not continue past
one: an event that cannot be applied ends the replay after the attempts, and the read models are
left as the *A replay fails partway* scenarios describe. A replay is a maintenance operation; the
fallback when one cannot complete is the state before it — the copy the framework keeps where the host
opted in to it, and otherwise the backup the operator took.

Re-applying a batch from its start relies on the guarantee projections already give: a second
application of the same event converges on the same state, because delivery is at-least-once.

#### Scenario: A batch fails once and then succeeds

- **WHEN** applying a batch fails on the first attempt and succeeds on a later one within the
  attempt limit
- **THEN** the replay continues with the next batch, the retried batch's entries have each been
  applied at least once, and the retry was recorded

#### Scenario: Reading a batch fails once and then succeeds

- **WHEN** reading a batch from the event store fails on the first attempt and succeeds on a later
  one within the attempt limit
- **THEN** the replay continues from that batch as if the read had succeeded the first time

#### Scenario: A batch fails on every attempt

- **WHEN** a batch fails on every attempt the policy allows
- **THEN** the replay fails, its failure is recorded, and it deactivates — the same ending as a
  failure that was never retried

#### Scenario: The host shuts down while a batch is being retried

- **WHEN** the host stops while the replay is between attempts
- **THEN** the replay ends without recording a failure, as it does for shutdown at any other moment
