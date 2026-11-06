## MODIFIED Requirements

### Requirement: A replayed event is applied under the session that produced it

While replaying, each event SHALL be processed under the session context recorded with it, not under
the replaying host's own.

Otherwise a rebuilt read model would attribute every row to whichever session happened to be
ambient, and tenant-scoped writes would land in the wrong tenant.

The recorded session SHALL be in place before the projection, and anything it depends on, are resolved
for the entry, so that a service that takes its tenant when it is constructed takes the entry's.
Consecutive entries recorded under one session MAY share what was resolved for them; entries recorded
under different sessions SHALL NOT.

#### Scenario: Events from several tenants are replayed

- **WHEN** a replay processes events recorded by different sessions
- **THEN** each is processed under its own recorded session context

#### Scenario: A projection's dependency takes its tenant when it is constructed

- **WHEN** a replay processes entries of several tenants and a projection depends on a service that
  takes the tenant when it is constructed
- **THEN** each entry is applied with that service constructed under the entry's own tenant
