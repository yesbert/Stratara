## MODIFIED Requirements

### Requirement: Verification applies on every path that consumes a bus message

Every consumer of a bus message — the command worker, the projection worker and the saga worker —
SHALL apply the same verification. A command the Orleans execution model resumes from the record it
wrote before the dispatch returned SHALL be verified in the same way under the same mode, because the
record carries the same envelope, with the same session context, through storage instead of a broker.

Signing on publication while only one consumer verifies leaves the others open, which is precisely
the defect this mechanism was introduced to fix.

#### Scenario: An event bundle reaches a projection or saga worker

- **WHEN** an event bundle arrives at a worker that builds read models or runs process managers
- **THEN** it is verified under the configured mode before any handler sees it

#### Scenario: A command reaches the command worker

- **WHEN** a command arrives at the command worker
- **THEN** it is verified under the configured mode before it is dispatched

#### Scenario: A recorded command is resumed from storage

- **WHEN** the execution model's drain resumes a recorded command on a host with a signer
- **THEN** it is verified under the configured mode before it is handed over, and a record that does
  not verify is refused in strict mode and recorded in permissive mode, with the unsigned and the
  invalid case distinguishable by the identity of the record
