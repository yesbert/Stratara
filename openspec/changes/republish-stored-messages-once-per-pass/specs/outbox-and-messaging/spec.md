## MODIFIED Requirements

### Requirement: A worker drains durable storage in batches under a distributed lock

A background worker SHALL periodically publish stored messages in batches, and SHALL hold a
lease-based lock while doing so, so that several instances of the same worker do not publish the same
message concurrently. A host that has registered the Orleans execution model MAY run the drain as
singleton work instead: it then runs once per cluster without a lock, resumes the stored commands
the execution model recorded, and needs no dedicated outbox host.

A drain pass SHALL be bounded by the work it can complete. Stored messages that could not be
published SHALL remain stored and be retried on a later pass, and SHALL NOT cause the current pass to
attempt them again. A pass SHALL NOT depend on storage coming back empty to end, because messages
that cannot be published never leave it.

A pass SHALL attempt each stored message once. The bounded retries of a first dispatch SHALL NOT apply
to republication: the message is already durable and the next pass is its retry, while retries inside a
pass would keep every message behind it waiting and could outlast the lock's lease.

#### Scenario: A worker acquires the lock

- **WHEN** the worker acquires the lock and stored messages exist
- **THEN** it publishes a batch of them and releases the lock afterwards

#### Scenario: A batch cannot be published

- **WHEN** a batch is handed to the dispatcher and none of it can be published
- **THEN** the pass ends rather than re-reading the same messages, the messages remain stored, and
  the next interval retries them

#### Scenario: A stored message is refused during a pass

- **WHEN** the bus refuses a stored message during a pass
- **THEN** that message is attempted once in the pass, stays stored, and is attempted once more on the
  next pass

#### Scenario: A refused message is followed by others

- **WHEN** the bus refuses a stored message and further messages follow it in the batch
- **THEN** each of them is attempted in the same pass, whichever topic it is for

#### Scenario: Stored messages of one kind cannot be published

- **WHEN** stored messages of one kind cannot be published
- **THEN** stored messages of the other kind are still attempted in the same pass

#### Scenario: Another instance holds the lock

- **WHEN** the worker cannot acquire the lock
- **THEN** it records that and skips the pass entirely, rather than draining concurrently

#### Scenario: Nothing is stored

- **WHEN** the worker acquires the lock and no stored messages exist
- **THEN** no dispatch happens and the lock is released

#### Scenario: A pass fails

- **WHEN** a drain pass fails
- **THEN** the failure is recorded and the worker continues on its next interval

#### Scenario: No lock implementation is configured

- **WHEN** no distributed lock is registered
- **THEN** a lock that always grants is used — correct for a single-instance deployment, and unsafe
  for several, so a multi-instance deployment must register a real one

#### Scenario: The drain runs as singleton work

- **WHEN** a host registers the drain as singleton work on the Orleans execution model
- **THEN** it runs once per period in the cluster, without a lock, and resumes stored commands whose
  hand-off was lost — verified with two silos
