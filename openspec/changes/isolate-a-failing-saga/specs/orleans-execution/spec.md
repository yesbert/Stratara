# orleans-execution

## MODIFIED Requirements

### Requirement: A failing entry stops its partition, is retried, and is visible

Where an entry cannot be applied — a missing prerequisite past its retry policy, or a genuine
failure — the reader SHALL stop at that entry, SHALL NOT advance its checkpoint past it, SHALL retry
it on the next wake-up or poll, and SHALL log the entry and count the stall, so that a partition that
stops advancing is seen and not inferred from a checkpoint that stands still. Where the failing entry
shares its position with entries before it that did apply — one transaction wrote them all — the
checkpoint SHALL stop below the whole group, and the group's applied entries SHALL be applied again
when the entry is retried, which a projection tolerates as it tolerates any second delivery. A read that fails
before any entry is applied — the store unreachable, a checkpoint the reader refuses — SHALL count as
a stall and be logged whichever wake-up or poll started it. A batch whose application is cut short by
the reader's own shutdown SHALL still record the checkpoint for the entries it applied. Each
store-reading saga SHALL read with a checkpoint of its own, so that an entry one saga cannot apply
stops that saga's reading of the partition only: every other saga SHALL apply the entry once and go on
past it, and SHALL NOT apply it again because another saga failed on it.

#### Scenario: A projection throws on one entry

- **WHEN** a projection throws while applying an entry
- **THEN** the checkpoint stays before that entry, the failure is logged with the entry's identity,
  the stall is counted, and the next wake-up tries the entry again

#### Scenario: A projection throws on the second entry of one transaction

- **WHEN** one transaction wrote two entries into a partition and a projection applies the first and
  throws on the second
- **THEN** the checkpoint stops below both, and the next read applies the first again before retrying
  the second

#### Scenario: A fact from another partition has not been applied yet

- **WHEN** a projection reports that a prerequisite is missing
- **THEN** the entry is retried under the preceding-fact policy without advancing the checkpoint,
  and applies once the prerequisite has

#### Scenario: The store cannot be read

- **WHEN** a reader's read of the store fails on a poll
- **THEN** the failure is logged with the consumer and partition, the stall is counted, and the next
  wake-up or poll reads again

#### Scenario: One saga throws on an entry another saga also handles

- **WHEN** two store-reading sagas react to the same fact and one of them throws on it on every attempt
- **THEN** the other applies the fact once and goes on to later facts of the partition, while the
  failing saga stays before the fact, logs it and counts the stall under its own consumer name

### Requirement: A read store's checkpoints belong to the consumers that read into it

A checkpoint SHALL be identified by the consumer that reads the store and the partition it reads, not
by the deployment that runs the consumer. Two deployments SHALL be able to keep their checkpoints in
one read store only when no consumer name is registered by both. Each store-reading saga SHALL be a
consumer of its own; the store-reading sagas of all deployments sharing a read store nonetheless count
as one set, because a saga registered later starts from the checkpoints the others hold, so at most one
deployment sharing a read store SHALL run them. The
documentation SHALL state this where a read store is configured for the execution model. A reader
that advances a checkpoint SHALL advance it only from the position it last saw: a write that finds
another position SHALL be refused with a message naming the position found and the one expected,
and the refused reader SHALL read the checkpoint again rather than trust its own, so that an
activation that outlived its successor never rewinds or overtakes what the successor wrote. A
checkpoint SHALL NOT be written under another reader's name than the one it holds — advancing or
replacing it — and such a write SHALL be refused with a message naming both readers, as a read under
another reader is; a host switching readers resets its checkpoints first, as documented.

#### Scenario: Two deployments share a read store with distinct projections

- **WHEN** two deployments register store-reading projections with different names against one read
  store and both run
- **THEN** each deployment's projections advance their own checkpoints, and neither deployment's
  reading or reset changes the other's

#### Scenario: Two deployments share a read store and both run store-reading sagas

- **WHEN** two deployments that share a read store both run store-reading sagas
- **THEN** the documentation names this as unsupported, because both write the same checkpoints

#### Scenario: A stale activation writes a checkpoint

- **WHEN** an activation that last saw a checkpoint at one position writes after another activation
  of the same reader has advanced it
- **THEN** the write is refused naming both positions, the successor's checkpoint stands, and the
  refused activation reads the checkpoint again before it reads the store again

#### Scenario: A checkpoint is written under another reader's name

- **WHEN** a checkpoint written by one reader is advanced or replaced under another reader's name
- **THEN** the write is refused with a message naming both readers, and the checkpoint is unchanged

### Requirement: A host can start its store readers at the head of a populated store

A host whose read models are current when it adopts the execution model SHALL be able to seed, once
and while no silo of its cluster runs, a checkpoint at the store's current head for every
store-reading projection and saga it registers and every partition where none exists, so that its
first start applies only what commits afterwards. Seeding SHALL NOT change a checkpoint that exists,
SHALL write under the name of the reader the host registers, and SHALL report how many checkpoints it
wrote and how many it left. A projection registered later without a checkpoint SHALL still start at the
beginning of the store. A saga registered later without a checkpoint SHALL start where the host's
sagas already read in that partition — the furthest checkpoint a saga of the host holds there, or the
one the host's sagas shared before each read with its own — and at the beginning of the store only
where no saga has read, so that adding a saga never runs its side effects for the store's history. The documentation SHALL name seeding as the step between migrating the schema
and the first start on a populated store, and SHALL state that a checkpoint is keyed by the consumer's
name, so that renaming a projection starts it at the beginning.

#### Scenario: A populated store is seeded before the first start

- **WHEN** a host with a populated event store and current read models seeds its checkpoints and then
  starts for the first time
- **THEN** no entry committed before the seeding is applied by any of its projections or sagas, and
  every entry committed afterwards is — verified on the PostgreSQL store with the native reader

#### Scenario: A consumer already has a checkpoint

- **WHEN** seeding runs on a read store in which one registered consumer already has checkpoints
- **THEN** those checkpoints keep their positions, the other consumers are seeded, and the report
  counts both

#### Scenario: A projection is added after the seeding

- **WHEN** a host registers a new projection after its checkpoints were seeded and starts
- **THEN** the new projection reads from the beginning of the store, and the seeded ones from their
  checkpoints

#### Scenario: A saga is added to a running deployment

- **WHEN** a host whose sagas have read part of the store registers a new saga and starts
- **THEN** the new saga reacts only to facts after the position the host's sagas had reached, and to
  none before it

#### Scenario: A deployment upgrades from sagas that shared a checkpoint

- **WHEN** a host whose sagas shared one checkpoint per partition starts with a version in which each
  saga has its own
- **THEN** every saga starts from the shared checkpoint of its partition, and no fact at or below it is
  applied again by a silo on the new version
