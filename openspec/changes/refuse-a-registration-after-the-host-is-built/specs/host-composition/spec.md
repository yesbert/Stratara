## ADDED Requirements

### Requirement: A registration made after the host was built is refused

A framework registration SHALL NOT change a service that a host already built from the service collection uses.
Where a registration adds to something registered earlier — a vocabulary declared in parts, options gathered from
several calls, the set of types the host trusts, the roles a silo takes or the works it runs once — and it is called
on a service collection the host has already been built from, it SHALL fail with an error that names the
registration and says to call it while the host is being configured, and it SHALL leave the running host's services
as they were. Called while the host is being configured, it SHALL behave as before.

A registration that silently changed a running host's services would take effect or not depending on what had
already read them, and would race with every reader.

#### Scenario: A catalog part is declared after the host was built

- **WHEN** a host is built and a permission or setting catalog part is then declared on its service collection
- **THEN** the declaration fails naming the registration, and the running host's catalog is unchanged

#### Scenario: A trusted type is added after the host was built

- **WHEN** a host is built and a trusted type is then registered on its service collection
- **THEN** the registration fails naming it, and the running host's resolver does not trust the type

#### Scenario: A registration is made while the host is being configured

- **WHEN** the same registrations are made before the host is built
- **THEN** they succeed and add to what earlier calls registered, as before
