# package-distribution Specification

## Purpose
Let a consumer adopt as much or as little of the framework as it needs — one package or all
twenty-five — without version arithmetic, without unwanted transitive dependencies, and without
losing documentation, licence clarity or the ability to step into the source.

## Requirements

### Requirement: Every package ships at one lockstep version

All packable packages SHALL be versioned together from a single version declaration and published
as a set, even when only one of them changed. A consumer SHALL therefore be able to pin every
Stratara package with one version value.

#### Scenario: A consumer upgrades

- **WHEN** a consumer references several packages and upgrades
- **THEN** the same version exists for every package it references, and no combination of package
  versions has to be resolved

#### Scenario: Only one package changed

- **WHEN** a release contains a change to only one package
- **THEN** every package is still published at the new version — a version gap would force a
  consumer to track which packages moved

### Requirement: A consumer never resolves a dependency that was not published

Where a packable package references another project in the repository, that project SHALL itself be
published, so that a package's declared dependencies always resolve from the feed.

#### Scenario: A package declares a dependency on a sibling

- **WHEN** a consumer installs any published package
- **THEN** every dependency it declares on a Stratara package resolves at the same version

### Requirement: Dependencies flow one way and never cycle

Packages SHALL be layered so that a package depends only on packages at or below its own layer,
and never on one above. There SHALL be no dependency cycles.

A consumer adopting only the contract package must not receive the database, message-broker and
web-framework dependencies of the layers built on top of it.

#### Scenario: A consumer adopts only the contracts

- **WHEN** a consumer installs only the foundational contract packages
- **THEN** no infrastructure dependency — database provider, message broker, cache, cloud SDK, web
  framework — arrives transitively

#### Scenario: A lean consumer needs encryption but not persistence

- **WHEN** a consumer installs the security package
- **THEN** it receives no persistence, messaging or cloud dependency

### Requirement: Every published type and member is documented

Every public type and every public member of a published package SHALL carry documentation, and the
build SHALL fail rather than publish one that does not.

#### Scenario: A public member has no documentation

- **WHEN** a public type or member of a packable project is added without a documentation comment
- **THEN** the build fails

#### Scenario: A consumer inspects the API

- **WHEN** a consumer's editor or documentation browser reads a published package
- **THEN** every public type and member it can see carries a description, supplied by the
  documentation file shipped alongside the assembly

### Requirement: Every package carries its own identity, licence and readme

Every published package SHALL declare its own description and tags, and SHALL ship a readme, the
shared icon, and the licence — both as a machine-readable expression and as the bundled licence
text.

#### Scenario: A consumer looks at a package listing

- **WHEN** a consumer views a published package on a feed
- **THEN** it shows a package-specific description, its own readme, the icon, and the licence as a
  recognised identifier rather than an embedded file only

### Requirement: A consumer can step into the framework's source

Published packages SHALL ship symbols and source-link information, built deterministically, so a
consumer's debugger can step from its own code into the framework's source at the exact commit the
package was built from.

#### Scenario: A consumer debugs into framework code

- **WHEN** a consumer steps into a framework call in a debugger with symbol servers enabled
- **THEN** the source shown is the source the package was built from

#### Scenario: The same commit is built twice

- **WHEN** the same commit is built twice under continuous integration
- **THEN** the outputs are identical

### Requirement: Release notes come from the changelog

A published package's release notes SHALL be extracted at pack time from the repository's changelog
section for the version being packed, so that the changelog is the only place release notes are
written.

#### Scenario: The changelog has a section for the version

- **WHEN** a package is packed at a version the changelog documents
- **THEN** the package's release notes are that changelog section

#### Scenario: The changelog has no section for the version

- **WHEN** a package is packed at a version the changelog does not document — a preview build, or a
  branch experimenting with a version
- **THEN** the build succeeds and the package ships without release notes, rather than failing

### Requirement: Published artefacts never reference internal-only material

No published artefact — documentation comment in a shipped assembly, documentation site page,
sample, readme or changelog — SHALL reference the project's internal working directory. That
directory is not part of the published repository, so a path into it resolves for a maintainer and
is a dead link for every consumer.

#### Scenario: A documentation comment references an internal path

- **WHEN** a shipped source file, documentation page, sample, readme or changelog entry references
  the internal working directory
- **THEN** the local verification gate fails before the change can be published

### Requirement: Documentation never names an API that does not exist

Every framework API symbol named in the documentation SHALL resolve to a real, publicly accessible
declaration in the source. A symbol that resolves to nothing, or only to a non-public declaration,
SHALL fail the verification gate.

A documented method a consumer cannot call is worse than an undocumented one: it is discovered
after the consumer has designed around it.

#### Scenario: A doc names a method that was renamed

- **WHEN** documentation names a framework symbol that no longer exists under that name
- **THEN** the verification gate fails

#### Scenario: A doc names an internal type as if it were consumable

- **WHEN** documentation presents a symbol that exists only as a non-public declaration
- **THEN** the verification gate fails

### Requirement: The framework never depends on a consumer application

No published package SHALL reference any application that consumes the framework, or contain
domain logic specific to one.

#### Scenario: A consumer needs domain-specific behaviour

- **WHEN** a consumer needs behaviour specific to its own domain
- **THEN** that behaviour lives in the consumer's own repository, and the framework offers an
  extension point rather than the behaviour

### Requirement: A release is published only from a version tag, and only with approval

Packages SHALL be published only in response to an explicit version tag, and reaching the public
registry SHALL additionally require a human approval. Neither a merge nor a push to the main branch
SHALL publish anything anywhere. There is no pre-release channel: a version that carries no tag is
obtainable from no feed.

#### Scenario: A change merges to the main branch

- **WHEN** a change merges to the main branch
- **THEN** no package is published, and a consumer receives the change only with the next release

#### Scenario: A public release is cut

- **WHEN** a version tag is pushed
- **THEN** the packages are built from that tag, and an approval is required before they reach the
  public registry

#### Scenario: A change does not warrant a release

- **WHEN** a change touches only tests, documentation or continuous integration
- **THEN** no version bump and no tag are required, and the change reaches consumers with the next
  release cut for another reason
