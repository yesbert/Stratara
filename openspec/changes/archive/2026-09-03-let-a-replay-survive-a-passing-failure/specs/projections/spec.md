## ADDED Requirements

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
left as the *A replay fails partway* scenario describes. A replay is a maintenance operation; the
fallback when one cannot complete is the backup taken before it, which is the operator's, not the
framework's.

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
