## ADDED Requirements

### Requirement: A session for work the platform starts can be built in one call

The framework SHALL offer a single documented way to build a session for work the platform starts
on a tenant's behalf: the caller names the tenant the work is done for, and the session carries the
reserved system actor identities, a fresh correlation identity, and a causation identity, so that
the work can append to a stream without a command having preceded it.

Every flow the framework itself starts without an inherited actor — a durable timer's handler, a
saga step, a background sweep — SHALL be documented as using that shape, so that an audit trail
records the platform as the actor and the tenant as the data owner, rather than recording the
tenant as having acted.

#### Scenario: A timer handler dispatches a command

- **WHEN** a handler for work the platform started builds a session in that way for the tenant the
  work belongs to, and dispatches a tenant-scoped command
- **THEN** the command is dispatched, and what it appends records the platform as the actor and the
  tenant as the data owner

#### Scenario: The built session appends without a preceding command

- **WHEN** such a session is used to append to a stream directly, without a command having been
  dispatched
- **THEN** the append carries a causation identity and is not refused for the lack of one

#### Scenario: A documented example is followed

- **WHEN** a consumer follows the documented example for a flow with no inherited actor
- **THEN** the example works under the framework's strict tenant isolation and with its stores'
  provenance requirements, rather than being refused by either
