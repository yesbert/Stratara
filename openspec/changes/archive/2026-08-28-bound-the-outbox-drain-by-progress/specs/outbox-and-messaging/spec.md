## MODIFIED Requirements

### Requirement: A worker drains durable storage in batches under a distributed lock

A background worker SHALL periodically publish stored messages in batches, and SHALL hold a
lease-based lock while doing so, so that several instances of the same worker do not publish the same
message concurrently.

A drain pass SHALL be bounded by the work it can complete. Stored messages that could not be
published SHALL remain stored and be retried on a later pass, and SHALL NOT cause the current pass to
attempt them again. A pass SHALL NOT depend on storage coming back empty to end, because messages
that cannot be published never leave it.

#### Scenario: A worker acquires the lock

- **WHEN** the worker acquires the lock and stored messages exist
- **THEN** it publishes a batch of them and releases the lock afterwards

#### Scenario: A batch cannot be published

- **WHEN** a batch is handed to the dispatcher and none of it can be published
- **THEN** the pass ends rather than re-reading the same messages, the messages remain stored, and
  the next interval retries them

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

### Requirement: Delivery is at least once, never at most once

A message that reaches durable storage SHALL be retried until the bus accepts it, and SHALL be
removed from storage only after acceptance. A handler MUST therefore be prepared to see the same
message more than once.

A stored message SHALL be counted as published only once the bus has accepted it, so that the count
reflects what was delivered rather than what was read from storage.

#### Scenario: A stored message is published successfully

- **WHEN** a stored message is later published and the bus accepts it
- **THEN** it is removed from durable storage and counted as published

#### Scenario: Publishing a stored message fails again

- **WHEN** publishing a stored message fails
- **THEN** it stays in durable storage for a later attempt — it is never dropped, and it is not
  counted as published
