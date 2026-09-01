## ADDED Requirements

### Requirement: A published version announces itself in the source repository

Every version that reaches the public registry SHALL be announced in the framework's public source
repository, under the tag it was published from, without a hand step after the tag is pushed. The
announcement SHALL carry the changelog section for that version as its notes, so that the registry
and the repository describe the same version from the same source.

An announcement of a prerelease SHALL be marked as one, and SHALL NOT displace the newest stable
version as the one the repository presents as current — matching what a consumer sees who resolves
from the registry without asking for prereleases.

A version that is not published SHALL NOT be announced. The announcement follows the publication; it
is never the thing that makes a version look released.

#### Scenario: A version is published

- **WHEN** the packages for a version tag reach the public registry
- **THEN** the repository announces that version under that tag, with the changelog section for the
  version as its notes, and the repository and the registry agree on which version is current

#### Scenario: A prerelease is published

- **WHEN** the packages for a prerelease tag reach the public registry
- **THEN** the announcement is marked as a prerelease, and the newest stable version remains the one
  the repository presents as current

#### Scenario: The changelog has no section for the version

- **WHEN** a version the changelog does not document is published
- **THEN** the version is announced without notes, and the release is not failed for the missing
  section — the same degradation the packages themselves make

#### Scenario: The same version is published a second time

- **WHEN** a version tag is published again after an announcement for it already exists
- **THEN** the release succeeds and the announcement is not duplicated

#### Scenario: The approval is declined

- **WHEN** the human approval that guards the public registry is declined for a version tag
- **THEN** nothing is published and nothing is announced
