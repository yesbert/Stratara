## ADDED Requirements

### Requirement: Bundles about one aggregate reach sagas one at a time within a process

Where the saga worker processes bundles in parallel, it SHALL ensure that two bundles whose events
belong to the same aggregate stream are not dispatched to sagas concurrently within one process,
while bundles about different aggregates continue to be dispatched in parallel.

The requirement *Sagas run in parallel with each other, and in order within themselves* speaks about
the sagas inside one bundle. This one speaks about bundles across consumers: the second fact about an
aggregate does not reach a saga while the first is still being handled next door. The guarantee is
per process.

#### Scenario: Two bundles about the same aggregate arrive concurrently

- **WHEN** two bundles whose events belong to one aggregate stream are handed to two parallel
  consumers of the same process at once
- **THEN** the second is dispatched only after the first has completed

#### Scenario: Two bundles about different aggregates arrive concurrently

- **WHEN** two bundles whose events belong to different aggregate streams are handed to two parallel
  consumers of the same process at once
- **THEN** both are dispatched in parallel

#### Scenario: The number of aggregates exceeds the number of locks

- **WHEN** more distinct aggregates are in flight than the framework holds locks for
- **THEN** correctness is preserved — two unrelated aggregates may serialise against each other, but
  two bundles about the same aggregate never dispatch concurrently

### Requirement: A saga can report that a fact's prerequisite has not been applied yet

The framework SHALL let a saga report that something the fact refers to does not exist yet, in the
same way a projection can, and SHALL retry the bundle within the process a bounded number of times
with a short backoff, holding no aggregate lock while it waits, before failing it as an unhandled
saga failure.

#### Scenario: The prerequisite arrives during the wait

- **WHEN** a saga reports a missing prerequisite, and what it waits for exists before the retries are
  exhausted
- **THEN** the bundle is dispatched on a later attempt and treated as processed

#### Scenario: The prerequisite never arrives

- **WHEN** a saga reports a missing prerequisite on every attempt
- **THEN** the bundle fails as an unhandled saga failure does, and the failure is recorded with the
  stream and the event type the saga named

#### Scenario: A saga fails for any other reason

- **WHEN** a saga throws anything other than the missing-prerequisite report
- **THEN** the bundle is not retried and fails on the first occurrence, as before

### Requirement: The saga worker's degree of parallelism is configurable

The number of parallel consumers the saga worker opens SHALL be configurable in the saga options.
Where the configured value is not a positive number, the worker SHALL fall back to the processor
count rather than to zero.

#### Scenario: A host configures one consumer

- **WHEN** the saga options set the degree of parallelism to one
- **THEN** the worker opens a single consumer, and bundles are dispatched in the order the transport
  delivers them

#### Scenario: A host configures nothing

- **WHEN** the saga options do not set a degree of parallelism
- **THEN** the worker opens one consumer per processor, as it did before the option existed

#### Scenario: A host configures an invalid value

- **WHEN** the saga options set the degree of parallelism to zero or a negative number
- **THEN** the worker falls back to the processor count rather than opening no consumer
