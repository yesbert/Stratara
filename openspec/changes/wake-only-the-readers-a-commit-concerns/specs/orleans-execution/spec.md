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
wake, in the partitions it touched, the readers whose projection or saga handles one of its facts; a
wake-up that is lost costs latency and never a fact, because a poll reads the store whenever the
reader's partition holds anything past the reader's position. Appending SHALL work, and every append SHALL be
positioned for the reader, whatever execution strategy the host's write context uses — one that retries on
failure included, where a transient failure SHALL run the append again as a whole. A committed fact SHALL reach every projection and
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

#### Scenario: A write context retries on failure

- **WHEN** a host's write context uses an execution strategy that retries on failure and the host keeps
  the partition positions for the portable reader
- **THEN** an append is saved and positioned — verified on SQLite with a retrying strategy through the
  framework's unit of work, where before every append failed

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

## ADDED Requirements

### Requirement: A commit costs work only for the readers it concerns

Where a host has registered the Orleans execution model for projections or sagas, a commit SHALL cost a read of
the store and a checkpoint write only for the readers whose consumer handles one of the facts it committed, and an
idle reader SHALL cost the store no read of its own. After a commit, the host SHALL wake, in each partition the
commit touched, only the readers of the projections and sagas that handle one of the committed event types, decided
as the reader decides when it applies them. A host that commits facts without hosting a reader, and has registered
the store wake-ups, SHALL wake the same set. A stateful saga process, which decides from the event itself whether
it handles it, SHALL be woken by every commit in its partitions. Where the waking silo cannot decide whether an event
is relevant to a reader, it SHALL wake the reader. Waking fewer readers SHALL NOT change what any projection or saga
applies.

A reader that reads entries and applies nothing for them SHALL keep its position past them, and SHALL answer a wait
for its projection from that position. It SHALL write that position as its checkpoint with the next batch that
applies something, once a bounded interval the host can configure has passed, or when it deactivates, whichever
comes first. A reader started again from its checkpoint after its silo died SHALL therefore re-read at most the
entries it had applied nothing for, and SHALL apply nothing for them. A pause for a rebuild or a replay SHALL discard
a position not yet written, so that a checkpoint returned to the beginning is not moved past entries the rebuild
must re-read. A batch that applies anything SHALL write the checkpoint before the reader reads again, as before.

A reader's poll SHALL read the store only when its partition holds anything past the reader's position. A silo SHALL
answer that from at most one question to the store per partition and part of the poll interval, whichever of its
readers asks, so that the idle cost of a silo grows with its partition count, not with its number of consumers. A
fact whose wake-up was lost SHALL be applied within a little over one poll interval. Where the host's committed
position reader cannot tell a partition's head without walking the partition, a poll SHALL read the store as before.

In a rolling deployment, a wake-up from a host of an earlier version, which names no event types, SHALL wake every
reader of the partitions it names, and a host of this version whose wake-up reaches a silo of an earlier version
SHALL have it delivered in the form that silo understands. A checkpoint written by either version SHALL be read by
the other. The documentation SHALL say which readers a commit wakes, when a reader writes its checkpoint past entries
it applied nothing for, what a restart re-reads, and what a lost wake-up costs.

#### Scenario: A commit of an event one projection handles

- **WHEN** a host registers several projections and sagas, and commits facts of an event type that one projection
  handles
- **THEN** that projection applies the facts, no other reader of the touched partitions reads the store for that
  commit, and no checkpoint other than that projection's is written. Verified on the PostgreSQL store with the
  native reader by counting checkpoint writes and the readers' store reads

#### Scenario: A commit of an event no consumer handles

- **WHEN** a host commits facts of an event type that no projection or saga it registers handles
- **THEN** no reader is woken for the commit and no checkpoint is written for it

#### Scenario: A stateful saga process and a fact it did not declare

- **WHEN** a host registers a stateful saga process and commits a fact of a type the process declares no handler for
- **THEN** the process's reader is woken and the process decides about the fact as it did before

#### Scenario: A client host wakes the readers through the cluster

- **WHEN** a host that hosts no store reader and has registered the store wake-ups commits facts of an event type
  one projection handles
- **THEN** that projection applies the facts well before its poll interval has passed, and the readers of the other
  projections and sagas in the partition do not read the store for that commit

#### Scenario: The readers that applied nothing write their checkpoint later

- **WHEN** a reader is woken, reads entries of which it applies nothing, and no further fact reaches it
- **THEN** its checkpoint is unchanged until the configured interval has passed or the reader deactivates, and is
  then at the position past those entries, and a wait for its projection that covers those entries answers that it
  reached the mark without waiting for the write

#### Scenario: A silo dies before a deferred checkpoint is written

- **WHEN** a reader's silo is killed after the reader read entries it applied nothing for and before it wrote their
  position
- **THEN** the reader started again on another silo reads those entries again, applies nothing for them, and applies
  every fact committed after them once. Verified on the PostgreSQL store with the native reader

#### Scenario: A rebuild pauses a reader that holds a position not yet written

- **WHEN** a projection is rebuilt while one of its readers holds a position it has not written
- **THEN** the reader's checkpoint is at the beginning when the read model is emptied, is not moved past the
  beginning by the reader before the rebuild resumes it, and the read model holds every fact of the store once the
  readers have caught up

#### Scenario: An idle silo

- **WHEN** a silo with many readers runs for several poll intervals and nothing is committed
- **THEN** the store answers no more than a few questions per partition and poll interval for all its readers
  together, and no reader reads entries or writes a checkpoint. Verified on the PostgreSQL store with the native
  reader and with the portable reader

#### Scenario: A wake-up is lost

- **WHEN** a fact is committed and its wake-up never reaches the reader that handles it
- **THEN** the reader applies the fact within a little over one poll interval

#### Scenario: A host of the earlier version commits during a rolling deployment

- **WHEN** a host of the earlier version commits facts while a silo of this version hosts the readers
- **THEN** every reader of the touched partitions is woken, as before, and each applies what it handles

#### Scenario: A host of this version wakes a silo of the earlier version

- **WHEN** a host of this version that registered the store wake-ups commits facts and the readers run on a silo
  that does not know the wake-up naming event types
- **THEN** the silo is woken in the earlier form and its readers apply the facts before their poll interval has
  passed. Verified on the wake-up's handling of the refused call, not in a cluster of mixed versions

#### Scenario: The documentation says what a commit costs

- **WHEN** an operator reads the operating guide for the execution model
- **THEN** it says which readers a commit wakes, when a reader writes its checkpoint past entries it applied nothing
  for, what a reader re-reads after its silo died, what a lost wake-up costs, and names the option that bounds the
  deferred write
