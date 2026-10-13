## ADDED Requirements

### Requirement: A deferred command carries the owner it names

A command that names the owner of its protected fields SHALL carry that owner with it — through
durable storage, through republication from it, and over the bus — beside its session context and not
in place of any part of it. The host that handles the command SHALL read its protected fields for the
owner the message carries, and SHALL run the handler under the session the message carries.

A command that names no owner SHALL be stored and published in the form it had before a command could
name one, so that a host on an earlier release reads and verifies it unchanged, and SHALL be read for
the session's data-owner tenant and acting user as before.

A command still waiting when its owner is erased SHALL reach its handler, with its protected fields
absent, as any record reads whose key was destroyed. The documentation SHALL say so where it describes
a handler for a command that names an owner.

A host on a release that cannot read the owner a message carries does not handle such a command with
its fields silently emptied: the read fails, and the message is retried and then kept like any other a
handler cannot take. The documentation SHALL therefore state the order of a rolling upgrade — every
host that handles commands takes the release before any host dispatches a command that names an owner.

#### Scenario: A command naming an owner is handled on another host

- **WHEN** a command naming a tenant as its owner is dispatched from a session without a tenant and
  handled by a command worker
- **THEN** the worker reads its protected fields for the named tenant and runs the handler under the
  dispatching session

#### Scenario: A command naming an owner is stored and republished

- **WHEN** the bus does not take a command that names an owner, so that it is stored and republished
  by the drain
- **THEN** the republished message still carries the owner, and its handler receives the protected
  fields

#### Scenario: The owner is erased while the command waits

- **WHEN** a tenant's erasure is performed while a command naming that tenant as its owner waits in
  durable storage
- **THEN** the stored command's protected fields can no longer be read, and when it is delivered its
  handler runs with those fields absent

#### Scenario: A command names no owner

- **WHEN** a command that names no owner is dispatched
- **THEN** the stored and the published message hold nothing they did not hold before a command could
  name an owner

#### Scenario: A command naming an owner reaches a host on an earlier release

- **WHEN** a command that carries a protected field and names another owner than its session does is
  delivered to a host on a release before a command could name one
- **THEN** that host fails to read it — in strict mode because the signature does not verify, otherwise
  because the field was not written for the session it assumes — and does not run the handler
