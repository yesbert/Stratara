## ADDED Requirements

### Requirement: A hand-over answers once the command is accepted

Where a host has registered the Orleans execution model's command dispatcher, the hand-over of a heavy
command and of a command that names no aggregate SHALL be answered once a silo has accepted the command
and its hand-over is being renewed, not once the command has run. No call that the dispatching host or a
resumption waits on SHALL stay open for the length of the command's run. A command that runs longer than
the runtime's response timeout SHALL therefore leave no timeout of its hand-over in the log of the host
that dispatched or resumed it, and none on the silo that runs it. The command SHALL still run once, to its
end, under the same lease, permit, completion, failure recording and resumption as before. A silo that
stops while the command runs SHALL still cancel it and leave it to be resumed. A hand-over that fails,
or that the silo does not accept within the response timeout, SHALL be logged with the command's
identity, the aggregate it names and its type, whatever kind of command it is. The documentation SHALL
say that a hand-over is not bounded by the response timeout and that only a forwarded command is, and
SHALL NOT say that a timeout of the runtime goes unlogged.

#### Scenario: A heavy command runs longer than the response timeout

- **WHEN** a heavy command is dispatched and its handler runs longer than the runtime's response timeout
- **THEN** the handler runs once to the end and its append is committed, the intent is completed, and
  neither the dispatching host nor the silo logs a response that did not arrive on time or an expired
  message dropped. Verified with a shortened response timeout on the PostgreSQL store, with the
  dispatching host joined as an Orleans client

#### Scenario: A command that names no aggregate runs longer than the response timeout

- **WHEN** a command that names no aggregate is dispatched and its handler runs longer than the runtime's
  response timeout
- **THEN** the handler runs once to the end, the intent is completed, and neither end logs a timeout of
  the hand-over. Verified with a shortened response timeout on the PostgreSQL store

#### Scenario: A resumed long command

- **WHEN** a heavy command, or a command that names no aggregate, is resumed after the grace and runs
  longer than the runtime's response timeout
- **THEN** it runs once, and the resumption logs no timeout of its hand-over

#### Scenario: The silo stops while an accepted long command runs

- **WHEN** a silo that accepted a heavy command, or a command that names no aggregate, stops while the
  handler is still running
- **THEN** the handler's token is cancelled within the deactivation budget, the intent is not completed,
  and the command is resumed after the grace and runs to its end

#### Scenario: The hand-over itself fails

- **WHEN** the hand-over of a heavy command, or of a command that names no aggregate, cannot be delivered
  or is not accepted within the response timeout
- **THEN** the dispatching host logs the failure with the command's identity and type, and the recorded
  command is resumed after the grace
