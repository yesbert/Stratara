## MODIFIED Requirements

### Requirement: Log event ids follow a partitioned schema that reserves a consumer range

Every log message the framework emits SHALL carry an event id from a published schema, partitioned
by subsystem within a reserved range. Ids outside that range SHALL be left to consumer applications,
so that a consumer's own ids can never collide with the framework's. The Orleans execution model
SHALL have a band of its own in that schema, so that its messages — a stalled partition, a resumed
command, a kept command, a released permit, a failed flush of completions — are as filterable as
any other subsystem's.

#### Scenario: An operator filters logs by event id

- **WHEN** an operator filters on a framework event id
- **THEN** it identifies one message shape from one subsystem, and the subsystem is derivable from
  the id's band

#### Scenario: A consumer assigns its own event ids

- **WHEN** a consumer application assigns event ids to its own log messages
- **THEN** it can do so without consulting the framework's schema, because the framework's range and
  the consumer's range do not overlap

#### Scenario: An operator filters the execution model's messages

- **WHEN** an operator filters on the execution model's band
- **THEN** every message the execution model emits is in it and no other subsystem's is

## ADDED Requirements

### Requirement: The execution model measures what it does

The Orleans execution model SHALL publish, from the framework's one meter, the measurements an
operator needs to see it working: entries applied per projection and partition, stalled partitions,
commands recorded, resumed and kept, completions flushed and the flush's failures, heavy-work
permits in use, and the age of the oldest unapplied entry per partition where the reader can tell.
The names SHALL be part of the published instrument contract.

#### Scenario: A partition falls behind

- **WHEN** a projection's partition stops applying
- **THEN** its stall counter rises and the age of its oldest unapplied entry grows, on the
  framework's meter under the published names

#### Scenario: Commands are kept for an operator

- **WHEN** a resumed command exhausts its bound
- **THEN** the kept counter rises and the kept command is identifiable from the log event
