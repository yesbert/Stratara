# orleans-execution

## MODIFIED Requirements

### Requirement: Projections and sagas read the store in commit order and never miss a committed fact

Where a host has registered the Orleans execution model for projections or sagas, each SHALL read
the event store from a checkpoint, in an order in which no entry at or below a checkpoint can still
commit later, and apply what it reads under the session recorded with each entry. The recorded
session SHALL be in place before the projection or the sagas, and anything they depend on, are
resolved for the entry, so that a service that takes its tenant or its user when it is constructed
— a connection routed per tenant, a read-model context that captures the tenant — takes the entry's;
one read of the store MAY return entries recorded under several sessions, and each SHALL be applied
as it would have been had it arrived on its own. A commit SHALL
wake the readers of the partitions it touched; a wake-up that is lost costs latency and never a
fact, because a poll reads the store regardless. A committed fact SHALL reach every projection and
saga whatever dies after the commit. Two rebuilds of one projection SHALL NOT interleave: the
projection's readers SHALL resume only when every rebuild that paused them has finished, and a
rebuild requested while a full replay is active SHALL be refused with a message that says so. A full
replay on a host whose projections read the store SHALL return every such projection's checkpoint to
the beginning before the read models are emptied, and the replay's own pass over the store SHALL be
followed by the readers' pass from the beginning once the replay ends, so that every store-reading
projection applies the store twice under a full replay and is correct because it applies idempotently;
the documentation SHALL say so where it describes the full replay and SHALL name the rebuild of a
single projection as the way to re-read a read model once. A
reader brought back for a partition the host's partition count no longer has SHALL retire itself —
stop returning, log that it did, and read nothing — so that lowering the count leaves no reader that
returns every keep-alive period to be refused.

Pausing the readers for a rebuild or a replay SHALL leave none of them paused where it does not
finish: a pause that fails SHALL resume the readers that did pause and SHALL fail naming what it
could not pause, so that no partition is left waiting for a resume that never comes. A pause SHALL
belong to the one who paused and SHALL last only while that pauser keeps it: a pauser that stops —
its process dies part-way through a rebuild — SHALL leave its readers paused for no longer than a
bounded lease, after which they resume and the lapse is logged naming the reader. Resuming SHALL
release only the resuming pauser's own pause, however often the resume is repeated, so that a retried
resume never releases a pause another rebuild still holds. A rebuild or a replay SHALL leave its read
models holding every fact of the store however early a reader resumes during it — through a lapse, or
an activation that moved and no longer knows it was paused — so that a fact a reader applied before
the read model was emptied is applied again after it. Returning a
checkpoint to the beginning SHALL be accepted whatever reader last wrote it — the beginning means
the same under every reader and every partition count — so that a deployment whose reader or
partition count changed can rebuild or replay from inside the running cluster, and the documentation
SHALL name that as the way to recover from a refused checkpoint. A wake-up that cannot be sent SHALL
be logged with the consumers it was meant for, so that a wake-up path that is always lost is visible
as more than latency, and one wake-up that fails SHALL NOT keep the other consumers of that commit
from being woken.

#### Scenario: The host dies between the commit and the wake-up

- **WHEN** a host is killed after committing events and before any wake-up or publication
- **THEN** every projection and saga applies those events after the host or another silo reads the
  store — verified with twenty kills on the PostgreSQL store, none lost and, the kill falling before
  any read, none applied twice

#### Scenario: Two transactions commit out of sequence order

- **WHEN** a transaction with the lower sequence number commits after one with a higher one
- **THEN** no reader skips the late committer — verified on the PostgreSQL store with the native
  reader and on any relational store with the portable reader

#### Scenario: A projection is rebuilt

- **WHEN** a rebuildable projection is asked to rebuild
- **THEN** only its read model is emptied and re-read from the beginning of the store, in parallel
  over the partitions, while every other projection keeps applying live events

#### Scenario: A rebuild fails part-way

- **WHEN** emptying a rebuildable projection's read model fails part-way through a rebuild
- **THEN** the projection re-reads from the beginning of the store when it resumes, and never applies
  on top of a partly emptied read model without re-reading

#### Scenario: A projection is asked to rebuild twice at once

- **WHEN** a second rebuild of a projection is requested while the first is still emptying its read
  model
- **THEN** the projection's readers do not resume until both have finished, and the read model holds
  every fact of the store once the readers have caught up

#### Scenario: A reader cannot be paused for a rebuild

- **WHEN** one partition's reader cannot be paused for a rebuild
- **THEN** the rebuild fails naming that partition, and no reader the rebuild reached stays paused

#### Scenario: A read model is rebuilt after the partition count changed

- **WHEN** a read model is rebuilt on a host whose checkpoints were written by another reader or under
  another partition count
- **THEN** the rebuild returns them to the beginning under the host's own reader and the read model is
  re-read, without stopping the deployment

#### Scenario: A wake-up cannot be sent

- **WHEN** a commit's wake-up cannot be delivered
- **THEN** it is logged naming the consumers it was meant for, and the consumers it could still reach are woken

#### Scenario: A rebuild is requested during a full replay

- **WHEN** a projection's rebuild is requested while a full replay is active
- **THEN** the rebuild is refused with a message naming the replay, and the replay is not disturbed

#### Scenario: A full replay runs on a host whose projections read the store

- **WHEN** a full replay is requested on a host whose projections read the store
- **THEN** every store-reading projection's checkpoint is at the beginning when its read model is
  emptied, its read model holds every fact once the readers have caught up after the replay, and a
  projection that counts its applications has applied each fact twice — verified on the PostgreSQL
  store with the native reader

#### Scenario: One read returns entries of several tenants

- **WHEN** a partition holds entries recorded under two tenants within one read, and a projection or
  saga depends on a service that takes the tenant when it is constructed
- **THEN** each entry is applied with that service constructed under the entry's own tenant, and none
  under the other's or under no tenant — verified on the PostgreSQL store with the native reader, for
  a projection and for a saga

#### Scenario: The partition count is lowered under the native reader

- **WHEN** a host that reads with the native reader lowers its partition count and its readers'
  checkpoints are reset as documented, and the keep-alive of a reader beyond the new count brings it
  back
- **THEN** that reader stops returning, reads nothing, logs that it retired, and no stall is counted
  for it — verified on the PostgreSQL store

#### Scenario: The process rebuilding a projection dies

- **WHEN** the process that paused a projection's readers for a rebuild dies before it resumes them
- **THEN** the readers resume by themselves once the pause's lease has passed, each lapse is logged
  naming the reader, and the projection's read model follows the store again without a silo restart

#### Scenario: A resume is repeated while a second rebuild holds the readers

- **WHEN** two rebuilds of one projection hold its readers paused, and the first rebuild's resume is
  delivered twice
- **THEN** the readers stay paused until the second rebuild resumes them

#### Scenario: A reader resumes while its read model is being emptied

- **WHEN** a reader of a projection being rebuilt resumes and applies facts before the read model is
  emptied
- **THEN** once the rebuild has finished and the readers have caught up, the read model holds every
  fact of the store, those facts included
