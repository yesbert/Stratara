## Purpose

Run commands, projections, sagas, timers and singleton work as virtual actors on a cluster, so that a
committed fact is never lost to a crash, one aggregate has one writer across the whole deployment,
and work that must happen once per cluster needs no lock.

## ADDED Requirements

### Requirement: One aggregate has one writer across the cluster

Where a host has registered the Orleans execution model for commands, a command that names an
aggregate SHALL run in that aggregate's activation, and two commands naming the same aggregate SHALL
NOT run concurrently anywhere in the cluster. The activation is registered in a storage-backed
directory a host provides. Where an unstable cluster produces a second activation regardless, the
store's version constraint SHALL still refuse the second writer, so the guarantee degrades to the one
the bus workers give and never below it.

#### Scenario: Two commands name one aggregate from two hosts

- **WHEN** two hosts dispatch commands naming the same aggregate at the same time
- **THEN** one runs after the other, in one activation, and no concurrency conflict is raised

#### Scenario: The cluster is unstable and activates an aggregate twice

- **WHEN** a directory lapse lets two activations of one aggregate exist
- **THEN** at most one of their appends succeeds and the other observes a concurrency conflict, as it
  would on the bus path

### Requirement: An accepted command is recorded before the call returns and resumed after a crash

Where a host has registered the Orleans execution model's command dispatcher, dispatching a command
SHALL record it durably before the dispatch returns and hand it to its activation afterwards. A
host that dies between acceptance and completion SHALL resume the command after a configurable
grace. Completion SHALL be recorded outside the handler's turn; a command whose completion the host
did not record before dying SHALL run again. A command whose handler keeps failing SHALL be resumed a
bounded number of times and then kept for an operator, as a bus message a handler cannot take is
kept, and SHALL NOT hold back the resumption of other commands.

#### Scenario: The host dies after acceptance

- **WHEN** the dispatch has returned and the host is killed before the handler ran
- **THEN** the command runs after the host or another silo resumes it — verified with twenty kills
  on the PostgreSQL store

#### Scenario: The host dies after the handler completed

- **WHEN** the handler completed and the host is killed before the completion was recorded
- **THEN** the command runs a second time, which the handler tolerates as it does under at-least-once
  delivery on the bus

#### Scenario: A handler keeps failing

- **WHEN** a resumed command's handler throws on every attempt
- **THEN** it is resumed up to the configured bound, then kept with the attempt count and the last
  failure, and the commands after it are still resumed

#### Scenario: Two commands to one aggregate from one scope

- **WHEN** one scope dispatches two commands naming the same aggregate, one after the other
- **THEN** they run in the order they were dispatched; across scopes no order is promised

### Requirement: Projections and sagas read the store in commit order and never miss a committed fact

Where a host has registered the Orleans execution model for projections or sagas, each SHALL read
the event store from a checkpoint, in an order in which no entry at or below a checkpoint can still
commit later, and apply what it reads under the session recorded with each entry. A commit SHALL
wake the readers of the partitions it touched; a wake-up that is lost costs latency and never a
fact, because a poll reads the store regardless. A committed fact SHALL reach every projection and
saga whatever dies after the commit.

#### Scenario: The host dies between the commit and the wake-up

- **WHEN** a host is killed after committing events and before any wake-up or publication
- **THEN** every projection and saga applies those events after the host or another silo reads the
  store — verified with twenty kills on the PostgreSQL store, none lost

#### Scenario: Two transactions commit out of sequence order

- **WHEN** a transaction with the lower sequence number commits after one with a higher one
- **THEN** no reader skips the late committer — verified on the PostgreSQL store with the native
  reader and on any relational store with the portable reader

#### Scenario: A projection is rebuilt

- **WHEN** a rebuildable projection is asked to rebuild
- **THEN** only its read model is emptied and re-read from the beginning of the store, in parallel
  over the partitions, while every other projection keeps applying live events

### Requirement: A failing entry stops its partition, is retried, and is visible

Where an entry cannot be applied — a missing prerequisite past its retry policy, or a genuine
failure — the reader SHALL stop at that entry, SHALL NOT advance its checkpoint past it, SHALL retry
it on the next wake-up or poll, and SHALL log the entry and count the stall, so that a partition that
stops advancing is seen and not inferred from a checkpoint that stands still.

#### Scenario: A projection throws on one entry

- **WHEN** a projection throws while applying an entry
- **THEN** the checkpoint stays before that entry, the failure is logged with the entry's identity,
  the stall is counted, and the next wake-up tries the entry again

#### Scenario: A fact from another partition has not been applied yet

- **WHEN** a projection reports that a prerequisite is missing
- **THEN** the entry is retried under the preceding-fact policy without advancing the checkpoint,
  and applies once the prerequisite has

### Requirement: Work that must happen once happens once per cluster

Singleton work SHALL run in one place in the cluster at its period, without a lock, and SHALL
resume elsewhere when the silo running it is lost. Owner-checked durable timers SHALL fire once per
cluster on or after their due time, SHALL fire for an owner that exists and never for one that was
removed, and SHALL survive a restart of the silo that registered them.

#### Scenario: Two silos run the same singleton work

- **WHEN** two silos register the same singleton work
- **THEN** it runs once per period in the cluster, not once per silo

#### Scenario: A timer's owner was removed before it was due

- **WHEN** a timer is due and its owner no longer exists
- **THEN** the timer unregisters itself and no handler runs

#### Scenario: The silo is killed with timers open

- **WHEN** a silo is killed with registered timers and restarted
- **THEN** every kept owner's timer fires exactly once and no removed owner's timer fires — verified
  with ten kills on the PostgreSQL reminder table

### Requirement: Heavy work is bounded across the cluster by permits that expire with their holder

Heavy commands SHALL run in a bounded worker pool per silo and under a cluster-wide bound of
permits. A permit SHALL expire when the silo holding it is no longer a member of the cluster or its
lease has lapsed, so that crashed workers do not shrink the bound. Interactive commands SHALL NOT
queue behind heavy work.

#### Scenario: A worker silo dies holding permits

- **WHEN** a silo holding heavy-work permits is killed
- **THEN** its permits are released once the cluster has declared it dead or the lease has lapsed,
  and the bound is whole again

#### Scenario: Interactive commands during a heavy burst

- **WHEN** interactive commands are dispatched while the heavy pool is saturated
- **THEN** their latency stays within its no-burst range — verified with five hundred heavy units
  of two hundred milliseconds against two hundred interactive commands

### Requirement: A host can reset what the execution model keeps outside the event stream

A host SHALL be able to clear, deterministically, everything the execution model keeps beside the
event stream — reminders, cluster membership, the grain directory and the checkpoints — so that a
deployment can be brought back to "nothing scheduled, nothing remembered". The event stream SHALL
NOT be touched by a reset.

#### Scenario: A reset is run

- **WHEN** a host runs the reset
- **THEN** no reminder, membership row, directory entry or checkpoint remains, the event stream is
  intact, and a restart fires nothing and rebuilds every checkpoint from the stream

### Requirement: A restart and a hard death behave as stated, and the documentation says so

A silo restarted on the endpoint it had SHALL be ready within seconds and SHALL fire a timer that
came due while it was down on its due time. A silo joining on a **different** endpoint while a
killed silo's membership entry is still active SHALL NOT be assumed to join: it waits for the
runtime's join window and then fails, and the documentation SHALL state the three answers — a second
active silo that votes the dead one out, a restart on the dead silo's endpoint, or a cleanup of the
membership table — rather than imply the runtime recovers on its own.

#### Scenario: A silo restarts on its own endpoint

- **WHEN** a single silo is killed and restarted on the same endpoint
- **THEN** it is ready in about a second and a timer registered before the kill fires at its due
  time — verified on Orleans 10.3.1 with the PostgreSQL membership table

#### Scenario: A replacement silo starts on another endpoint after a hard death

- **WHEN** the only silo is killed and a replacement starts on another endpoint
- **THEN** the replacement does not join within the runtime's join window, and the operations
  documentation names the three ways out — verified on Orleans 10.3.1 with the PostgreSQL
  membership table, under the default and the shortened liveness settings alike

### Requirement: The execution model can be adopted per role beside the bus workers

Each role — commands, projections, sagas, outbox drain, timers, heavy work — SHALL be adopted with
one registration after the role's existing composite, and a host SHALL be able to run both models
at once during a rollout, because both apply idempotently. A host that registers the execution model
without the storage-backed grain directory it requires SHALL fail at start with a message naming
what is missing, not at the first activation.

#### Scenario: A host adopts the projection role

- **WHEN** a host calls the projection composite and then the execution model's projection
  registration
- **THEN** the bus-fed projection worker is not registered and the store-reading projections are

#### Scenario: A host forgets the directory

- **WHEN** a host registers the execution model but no storage-backed grain directory under the
  name the model selects
- **THEN** the silo fails to start with a message that names the missing registration
