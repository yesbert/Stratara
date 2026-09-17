## ADDED Requirements

### Requirement: A host can start its store readers at the head of a populated store

A host whose read models are current when it adopts the execution model SHALL be able to seed, once
and while no silo of its cluster runs, a checkpoint at the store's current head for every
store-reading projection and saga it registers and every partition where none exists, so that its
first start applies only what commits afterwards. Seeding SHALL NOT change a checkpoint that exists,
SHALL write under the name of the reader the host registers, and SHALL report how many checkpoints it
wrote and how many it left. A consumer registered later without a checkpoint SHALL still start at the
beginning of the store. The documentation SHALL name seeding as the step between migrating the schema
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
