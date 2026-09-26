## ADDED Requirements

### Requirement: A recorded command whose events were committed is not run again

Where a recorded command's handler fails with the event store's failure that says its events were
committed but could not be published, the execution model SHALL complete the command instead of
recording a failed attempt and resuming it, and SHALL log the failure with the command's identity and
type at error level. Resuming it would record the same facts again.

#### Scenario: A recorded command committed but could not publish

- **WHEN** a recorded command's handler fails because its save committed its events but could not
  hand their bundle on
- **THEN** the command is completed, no failed attempt is recorded, it is not resumed, and the failure
  is logged with the command's identity

### Requirement: A framework failure keeps its type between silos

A failure the framework defines — a concurrency conflict, a save that committed but could not publish —
thrown on one silo SHALL reach a caller on another silo, or a client, with its type, its message and
its inner failure, so that the caller treats it as it would in process. The registrations of the
execution model SHALL arrange this without the host having to. Properties of such a failure beyond
those need not cross; where they do not, they SHALL read as empty rather than fail, and the message
SHALL carry what they said.

#### Scenario: A handler on another silo reports a conflict

- **WHEN** a command forwarded to an aggregate on another silo fails with a concurrency conflict
- **THEN** the caller receives a concurrency conflict, and a transport that runs the forwarding
  handler treats it as a conflict rather than a failure — verified on the serializer round trip the
  runtime uses between silos

#### Scenario: A handler on another silo committed but could not publish

- **WHEN** a command forwarded to another silo fails because its save committed but could not
  publish
- **THEN** the caller receives that failure with its type, so nothing runs the command again
