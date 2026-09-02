## ADDED Requirements

### Requirement: Bundles about one aggregate are applied one at a time within a process

Where the projection worker processes bundles in parallel, it SHALL ensure that two bundles whose
events belong to the same aggregate stream are not applied concurrently within one process, while
bundles about different aggregates continue to be applied in parallel.

This is the same guarantee the command side gives for commands naming one aggregate. Without it, a
follow-up fact handed to one consumer can be applied before the fact that created the entity, which
is still in flight on another. The guarantee is per process: two processes consuming the same
subscription do not serialise against each other.

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

### Requirement: A projection can report that a fact's prerequisite has not been applied yet

The framework SHALL offer a projection a way to say that the entity a fact refers to does not exist
in its read model yet, distinct from any other failure. A bundle reported that way SHALL be retried
within the process a bounded number of times with a short backoff, holding no aggregate lock while it
waits, and SHALL fail as an unhandled projection failure only once those retries are exhausted.

The distinction is the point: "the beginning has not arrived" and "the beginning will never arrive"
produce the same observation in a projection, and only time tells them apart. A short wait resolves
the first without turning the second into a poison message.

#### Scenario: The prerequisite arrives during the wait

- **WHEN** a projection reports a missing prerequisite, and the fact that creates the entity is
  applied before the retries are exhausted
- **THEN** the bundle is applied on a later attempt and treated as processed

#### Scenario: The prerequisite never arrives

- **WHEN** a projection reports a missing prerequisite on every attempt
- **THEN** the bundle fails as an unhandled projection failure does, and the failure is recorded with
  the stream and the event type the projection named

#### Scenario: A projection fails for any other reason

- **WHEN** a projection throws anything other than the missing-prerequisite report
- **THEN** the bundle is not retried and fails on the first occurrence, as before

#### Scenario: A waiting bundle does not block the fact it waits for

- **WHEN** a bundle reports a missing prerequisite and the creating fact for the same aggregate is
  handed to another consumer of the same process
- **THEN** the creating fact is applied while the first bundle waits, rather than waiting behind it

### Requirement: The projection worker's degree of parallelism is configurable

The number of parallel consumers the projection worker opens SHALL be configurable in the
projection options. Where the configured value is not a positive number, the worker SHALL fall back
to the processor count rather than to zero.

#### Scenario: A host configures one consumer

- **WHEN** the projection options set the degree of parallelism to one
- **THEN** the worker opens a single consumer, and bundles are applied in the order the transport
  delivers them

#### Scenario: A host configures nothing

- **WHEN** the projection options do not set a degree of parallelism
- **THEN** the worker opens one consumer per processor, as it did before the option existed

#### Scenario: A host configures an invalid value

- **WHEN** the projection options set the degree of parallelism to zero or a negative number
- **THEN** the worker falls back to the processor count rather than opening no consumer
