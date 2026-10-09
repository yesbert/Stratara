## ADDED Requirements

### Requirement: A new stream's owner can be stated at creation, and a host can require it to be

A caller SHALL be able to create a stream with an owner it states, and that owner SHALL be the one the
stream records, so every later event on the stream for which no subject is stated keeps it. Creating with
a stated owner SHALL fail as creating does when the stream already exists, and SHALL fail as an explicit
subject does when the stated owner names no tenant.

A host SHALL be able to decide what happens when the first event of a stream that does not exist yet
would take its owner from the session — that is, when no subject is stated for it and it carries no tenant
of its own as a creation event. The host SHALL be able to allow it, as before; to allow it and record a
warning naming the stream, the event and the tenant taken from the session; or to refuse it, in which case
the append SHALL fail before anything is staged, with a message naming the stream, the event and the ways
to state an owner. Allowing it SHALL be the default. The decision SHALL NOT affect an event whose owner is
stated, a creation event that carries a tenant, or any append to a stream that already exists.

The session decides only the first event's owner now that a stream keeps the owner it was created with —
but it decides it silently, and the session belongs to whoever is acting: an operator creating a record in
someone else's tenant keys the record's whole history to their own.

#### Scenario: A stream is created with a stated owner

- **WHEN** a stream is created with a stated owner, and an event is appended to it later under a session
  naming another tenant
- **THEN** both events are recorded for the stated owner

#### Scenario: A stream that exists is created with a stated owner

- **WHEN** creation with a stated owner is attempted for a stream that already exists
- **THEN** it fails, with a message naming the stream and directing the caller to append instead

#### Scenario: The stated owner names no tenant

- **WHEN** a stream is created with a stated owner whose tenant is absent
- **THEN** the creation fails with a message naming the event and the stream, and nothing is staged

#### Scenario: The host allows the session to decide

- **WHEN** the host has made no decision, and a new stream's first event names no tenant and no owner is
  stated
- **THEN** the event is recorded for the session's tenant, as before, and nothing is recorded about it

#### Scenario: The host asks to be warned

- **WHEN** the host chose to be warned, and a new stream's first event takes its owner from the session
- **THEN** the event is recorded for the session's tenant, and a warning names the stream, the event type
  and that tenant

#### Scenario: The host refuses

- **WHEN** the host chose to refuse, and a new stream's first event would take its owner from the session
- **THEN** the append fails before anything is staged, with a message naming the stream, the event type and
  the ways to state an owner

#### Scenario: The host refuses, and the owner is stated another way

- **WHEN** the host chose to refuse, and a new stream is created with a stated owner, or with a creation
  event that carries a tenant, or an event is appended to a stream that already exists
- **THEN** the event is recorded as it would be without the decision
