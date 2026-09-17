## MODIFIED Requirements

### Requirement: A projection can read the store from a checkpoint instead of consuming the bus

On the Orleans execution model a projection SHALL be fed from the event store in commit order from
a checkpoint per partition, under the session recorded with each entry, with a commit as the
wake-up and a poll as the safety net; the bus SHALL be optional for projections on that model, and
a host MAY keep publishing bundles so that both paths run — whichever applies a fact first wins,
idempotently. Every guarantee of this capability that speaks of a bundle SHALL hold for an entry
read from the store. The recorded session SHALL be in place before the projection and what it
depends on are resolved for an entry, as it is before the projection manager is resolved for a
bundle, so that a projection whose dependencies take the tenant or the user when they are
constructed sees the entry's, whatever the entries around it were recorded under.

#### Scenario: A fact is committed

- **WHEN** events are committed on a host with store-reading projections
- **THEN** the projections apply them after the wake-up, within the latency the push path gives —
  verified at two thousand events per run on the PostgreSQL store

#### Scenario: The wake-up is lost

- **WHEN** a commit's wake-up never reaches a reader
- **THEN** the poll applies the events at its next interval, and nothing is lost

#### Scenario: A projection's dependency takes its tenant at construction

- **WHEN** entries of two tenants are read from the store within one read, and the projection depends
  on a service that reads the ambient tenant when it is constructed
- **THEN** the service the projection receives for each entry was constructed under that entry's
  tenant — verified on the PostgreSQL store with the native reader
