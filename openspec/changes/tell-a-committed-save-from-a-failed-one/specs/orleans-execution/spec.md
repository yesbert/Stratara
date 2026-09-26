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
