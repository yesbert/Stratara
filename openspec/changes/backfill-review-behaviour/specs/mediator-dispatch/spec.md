## MODIFIED Requirements

### Requirement: A command may be recorded before its handler runs

The framework SHALL offer an opt-in stage that durably records each dispatched command before the
handler executes, so that an audit trail exists even for a command whose handler fails.

Recording a command makes its identity the session's causation. A command dispatched while the session
already carries a causation — from inside another command's handler — SHALL hand that causation back
when it returns or fails, so that what the outer handler appends afterwards names the outer command as
its cause.

#### Scenario: A command is dispatched with auditing enabled

- **WHEN** a command is dispatched and auditing is registered
- **THEN** the command is recorded, and only then does the handler run

#### Scenario: A query is dispatched with auditing enabled

- **WHEN** a request that is not a command is dispatched
- **THEN** nothing is recorded, and the request proceeds

#### Scenario: A handler dispatches another command and goes on

- **WHEN** with auditing registered, a handler dispatches a second command, which returns, and then
  appends an event
- **THEN** the event carries the identity of the outer command as its causation

#### Scenario: A nested command fails

- **WHEN** with auditing registered, a command dispatched from inside a handler fails and that handler
  catches the failure
- **THEN** the session's causation is the outer command's again

#### Scenario: The outermost command returns

- **WHEN** a command dispatched under a session that carried no causation has returned
- **THEN** the session's causation is that command's identity, for what runs after it in the same scope
