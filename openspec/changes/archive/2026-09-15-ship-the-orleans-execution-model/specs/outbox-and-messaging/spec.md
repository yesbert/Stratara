## MODIFIED Requirements

### Requirement: Dispatch attempts the bus first and falls back to durable storage

Dispatching a command SHALL attempt to publish to the message bus, and SHALL write the message to
durable storage only if that attempt fails. A caller SHALL NOT be able to observe which path was
taken.

A host that has registered the Orleans execution model's command dispatcher instead SHALL record
every command in durable storage before the dispatch returns and hand it to its activation
afterwards; the promise the caller relies on — an accepted command is not lost — is the same, and
what changes is only what a dying host costs: nothing is lost, and a handler may run a second time.
The stored record is removed shortly after the handler has completed.

For an event bundle the same holds by default. A host MAY instead opt in to durable bundles: the
bundle is then written to durable storage in the same transaction that commits its events, the bus
is attempted after the commit, and the stored bundle is removed once the bus has accepted it — so
that from the moment the events are durable, the bundle is durable too. A caller SHALL NOT be able
to observe which path was taken here either; what it can observe is the guarantee stated under
*Delivery is at least once, never at most once*.

The outbox is therefore a fallback for commands, and for bundles a fallback or a write-ahead
record, as the host chooses: the common case never touches the database unless the host has decided
that a committed fact must never be left behind.

#### Scenario: The bus accepts the message

- **WHEN** the bus accepts a dispatched message
- **THEN** nothing is written to durable storage

#### Scenario: The bus rejects or is unreachable

- **WHEN** publishing fails for any reason
- **THEN** the failure is recorded and the message is written to durable storage, and the caller's
  dispatch still succeeds

#### Scenario: The caller needs a handle on the dispatch

- **WHEN** a command is dispatched
- **THEN** the caller receives the identity assigned to it, whichever path it took

#### Scenario: A host has opted in to durable bundles and the bus accepts

- **WHEN** a save commits events on a host with durable bundles and the bus accepts the bundle
- **THEN** the bundle was in durable storage when the events became durable, and is removed after
  acceptance — verified on the PostgreSQL store

#### Scenario: A host has opted in to durable bundles and the bus refuses

- **WHEN** a save commits events on a host with durable bundles and the bus refuses the bundle
- **THEN** the bundle stays in durable storage for the drain, and the save succeeds without a second
  write

#### Scenario: A host dispatches through the Orleans execution model

- **WHEN** a command is dispatched on a host that registered the execution model's dispatcher
- **THEN** the command is in durable storage when the dispatch returns, the caller receives its
  identity, and the record is gone once the handler has completed — verified on the PostgreSQL store

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

#### Scenario: The drain runs as singleton work

- **WHEN** a host registers the drain as singleton work on the Orleans execution model
- **THEN** it runs once per period in the cluster, without a lock, and resumes stored commands whose
  hand-off was lost — verified with two silos

## ADDED Requirements

### Requirement: Stored messages can be removed as a batch

The outbox repository port SHALL offer removal of a set of stored messages in one call, with a
default that removes them one by one, so that an execution model completing many commands at once
does not pay one round trip per completion and a consumer's own repository implementation keeps
working without change.

#### Scenario: A consumer implements the repository port

- **WHEN** a consumer's implementation of the port predates the batch removal
- **THEN** it still compiles and behaves, removing the batch one message at a time

#### Scenario: The framework's repository removes a batch

- **WHEN** the framework's own repository is asked to remove a batch
- **THEN** the batch is removed in one round trip to the database — verified on the PostgreSQL store
