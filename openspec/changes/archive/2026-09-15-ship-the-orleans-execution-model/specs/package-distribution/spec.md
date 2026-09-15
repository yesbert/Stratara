## MODIFIED Requirements

### Requirement: Every package ships at one lockstep version

All packable packages SHALL be versioned together from a single version declaration and published
as a set, even when only one of them changed. A consumer SHALL therefore be able to pin every
Stratara package with one version value. The Orleans execution model's packages SHALL be members of
that set and SHALL declare their runtime dependency as a range bounded by the next major of the
runtime, so that a consumer hosting its own runtime version inside the range is not forced to
change it by a Stratara patch.

#### Scenario: A consumer upgrades

- **WHEN** a consumer references several packages and upgrades
- **THEN** the same version exists for every package it references, and no combination of package
  versions has to be resolved

#### Scenario: Only one package changed

- **WHEN** a release contains a change to only one package
- **THEN** every package is still published at the new version — a version gap would force a
  consumer to track which packages moved

#### Scenario: A consumer hosts a newer runtime minor

- **WHEN** a consumer references the execution model's packages and a runtime minor newer than the
  one they were built against, inside the declared range
- **THEN** the packages restore and run, and a Stratara patch does not move the range

### Requirement: Dependencies flow one way and never cycle

Packages SHALL be layered so that a package depends only on packages at or below its own layer,
and never on one above. There SHALL be no dependency cycles.

A consumer adopting only the contract package must not receive the database, message-broker and
web-framework dependencies of the layers built on top of it. The execution model's packages SHALL
sit on the top layer: no package below them SHALL depend on the runtime, so a consumer that does
not adopt the execution model receives no runtime dependency.

#### Scenario: A consumer adopts only the contracts

- **WHEN** a consumer installs only the foundational contract packages
- **THEN** no infrastructure dependency — database provider, message broker, cache, cloud SDK, web
  framework — arrives transitively

#### Scenario: A lean consumer needs encryption but not persistence

- **WHEN** a consumer installs the security package
- **THEN** it receives no persistence, messaging or cloud dependency

#### Scenario: A consumer stays on the bus workers

- **WHEN** a consumer installs every package but the execution model's
- **THEN** no actor-runtime dependency arrives transitively
