## MODIFIED Requirements

### Requirement: A replay is requested, not scheduled

A replay SHALL run only when explicitly requested. A host running the replay worker SHALL NOT begin
one on start-up.

A request SHALL carry an identity, which the requester MAY choose and which the progress and the outcome
of the replay it started SHALL name. Among the hosts that share the replay coordination state, a request
SHALL start at most one replay, however many of them run the replay worker. A request that arrives while
a replay is active SHALL NOT start another one, and that it was not run SHALL be recorded, because a
second replay begun on top of a running one empties what the first is rebuilding.

#### Scenario: A host starts with the replay worker registered

- **WHEN** a host containing the replay worker starts and nothing requests a replay
- **THEN** the worker subscribes for requests and no replay runs

#### Scenario: A replay is requested

- **WHEN** a replay is requested
- **THEN** the worker begins one

#### Scenario: Several hosts run the replay worker

- **WHEN** several hosts that share the coordination state run the replay worker, and one replay is
  requested
- **THEN** exactly one of them runs it, and the others start nothing — verified on the Redis-backed
  coordination store

#### Scenario: A replay is requested while one is running

- **WHEN** a replay is requested while another is active
- **THEN** no second replay starts, the running one continues undisturbed, and the request that was not
  run is recorded

#### Scenario: The requester chooses the identity

- **WHEN** a replay is requested with an identity the requester chose
- **THEN** the running replay's progress and, once it has ended, its outcome name that identity

### Requirement: A replay reports progress and failure

A replay SHALL publish the total number of events to replay and how many it has processed, and SHALL
record a failure message when it fails, so an operator can distinguish "still running" from "stopped
part way".

Once a replay has ended, its outcome SHALL remain readable: the identity of the request it ran, when it
started and when it ended, how many events it replayed, and whether it succeeded, failed or was
interrupted by its host stopping, with the failure message where it failed. The outcome SHALL be kept
until the next replay ends, so it is readable while the next one runs, and SHALL be readable from every
host that shares the coordination state. A reader that polls SHALL therefore be able to tell a replay that
finished between two polls from one that never ran. Once a replay has ended, its processed count and
total SHALL read as zero on every coordination state; the count it reached belongs to its outcome. A
replay that ends after its marking lapsed and another replay began SHALL record its outcome and SHALL NOT
end the other replay's marking. Only the host stopping SHALL make a replay end as interrupted; any other
cancellation is a failure.

#### Scenario: A replay is running

- **WHEN** a replay is in progress
- **THEN** its processed count and total are readable, and a completion percentage is derivable
- **AND** a total of zero yields a defined percentage rather than a division failure
- **AND** the identity of the request it is running is readable

#### Scenario: A replay fails

- **WHEN** a replay fails
- **THEN** the failure message is recorded, the active flag is cleared, and a very long message is
  truncated rather than stored whole
- **AND** its outcome reads failed, with the message and the number of events it had replayed

#### Scenario: The host shuts down during a replay

- **WHEN** a replay is interrupted by host shutdown
- **THEN** it is not recorded as a failure — shutdown is not a replay error
- **AND** its outcome reads interrupted, not succeeded, with the number of events it had replayed

#### Scenario: A replay ends between two polls

- **WHEN** a replay starts and succeeds entirely between two reads of the progress
- **THEN** the second read reports no active replay and an outcome that names the request, reads
  succeeded, and states how many events were replayed

#### Scenario: A replay outlived its lease while another began

- **WHEN** a replay's marking lapses, another request starts a replay, and the first replay then ends
- **THEN** its outcome is recorded, and the second replay is still marked active under its own request —
  verified on the in-process and the Redis-backed coordination state

#### Scenario: Before any replay

- **WHEN** no replay has ever ended against the coordination state
- **THEN** the progress reports no outcome, which is distinguishable from an outcome of any kind

#### Scenario: A failed replay's counters after it ended

- **WHEN** a replay has failed and the progress is read
- **THEN** the processed count and total read as zero on the in-process and on the Redis-backed
  coordination state alike
