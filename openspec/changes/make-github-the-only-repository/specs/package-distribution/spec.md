## MODIFIED Requirements

### Requirement: Published artefacts never reference internal-only material

No published artefact — documentation comment in a shipped assembly, documentation site page,
sample, readme or changelog — SHALL reference the project's internal working directory. That
directory is not part of the published repository, so a path into it resolves for a maintainer and
is a dead link for every consumer.

#### Scenario: A documentation comment references an internal path

- **WHEN** a shipped source file, documentation page, sample, readme or changelog entry references
  the internal working directory
- **THEN** the local verification gate fails before the change can be published

## REMOVED Requirements

### Requirement: Publication has distinct lanes and a public release is deliberate

**Reason**: The internal pre-release feed was retired when the project became a single public
repository. Nothing is published between tags any more, so a requirement guaranteeing a pre-release
build on every merge describes a distribution channel that no longer exists. The deliberate half of
it — a public release happens only on a tag, and only behind an approval — is preserved by the
requirement that replaces it.

**Migration**: A consumer that tracked pre-release builds between merges pins a released version and
waits for the next tag instead. Versions already published to the internal feed keep resolving; no
new ones appear there.

## ADDED Requirements

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
