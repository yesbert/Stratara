## MODIFIED Requirements

### Requirement: A release is published only from a version tag, and only with approval

Packages SHALL be published only in response to an explicit version tag, and reaching the public
registry SHALL additionally require a human approval. Neither a merge nor a push to the main branch
SHALL publish anything anywhere: a version that carries no tag is obtainable from no feed.

A tag MAY name a prerelease. A prerelease tag SHALL publish packages that carry that prerelease
identity, through the same approval as a stable release, and SHALL NOT be offered to a consumer who
resolves without asking for prereleases. Tagging a prerelease SHALL NOT require the branch to state
a different version than the one it is working toward.

#### Scenario: A change merges to the main branch

- **WHEN** a change merges to the main branch
- **THEN** no package is published, and a consumer receives the change only with the next release

#### Scenario: A public release is cut

- **WHEN** a version tag is pushed
- **THEN** the packages are built from that tag, and an approval is required before they reach the
  public registry

#### Scenario: A prerelease is cut ahead of the stable version

- **WHEN** a prerelease tag for a version is pushed
- **THEN** the packages are published carrying that prerelease identity, behind the same approval,
  and the version they are a prerelease of stays available to be released later

#### Scenario: A consumer installs without asking for prereleases

- **WHEN** a consumer resolves a package while a prerelease of a later version is published
- **THEN** the consumer receives the newest stable version, and reaching the prerelease requires
  asking for one explicitly

#### Scenario: Several prereleases precede one stable release

- **WHEN** more than one prerelease of the same version is published in turn
- **THEN** each is ordered ahead of its predecessor and behind the stable version, and no prerelease
  identity is ever reused

#### Scenario: A change does not warrant a release

- **WHEN** a change touches only tests, documentation or continuous integration
- **THEN** no version bump and no tag are required, and the change reaches consumers with the next
  release cut for another reason
