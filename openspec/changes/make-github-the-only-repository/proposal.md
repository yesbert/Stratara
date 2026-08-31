> **Status:** approved

# Make GitHub the only repository

## Why

Stratara is developed in a private Azure DevOps repository and published to `yesbert/Stratara` as a
one-way mirror: one squashed commit per merged pull request, an allowlist deciding what a consumer
may see, and a workflow that automatically closes any pull request opened against the public repo.
A consumer therefore reads a redacted projection of the project rather than the project. They cannot
open a pull request, cannot see the reasoning behind a release, and cannot read the specifications
that *are* the framework's contract — `openspec/` is excluded from the mirror by construction.

The mirror exists for one reason: the repository carries an internal working-context directory that
no consumer should read. Move that directory out, and the entire apparatus — the allowlist, the sync
scripts, the cleanliness scan, the pipeline that runs them — has nothing left to defend. What
replaces roughly a thousand lines of sync machinery is one line in `.gitignore`.

**No package, no public type and no API surface changes.** What changes is where the project lives,
how it is built, and what a consumer can see and do — plus one thing a consumer relies on directly:
how packages reach them. That last one is a guarantee, and it carries a spec delta.

## What Changes

- **GitHub becomes the only repository.** `yesbert/Stratara` is where development happens: branches,
  pull requests, issues, releases. Azure DevOps is frozen read-only as the historical archive.
- **Pull requests become the contribution path.** The workflow that auto-closes them is removed,
  and `CONTRIBUTING.md` — which currently explains that a pull request here is pointless — says how
  to open one instead.
- **`openspec/` becomes public**, specifications and archived changes together. The specs are the
  contract; the archive carries the reasoning that the dissolved decision records used to hold, so
  publishing one without the other would mean the rationale was deleted rather than moved. The
  archived changes that predate this decision and were written for internal readers stay behind.
- **CI moves to GitHub Actions**, replacing five Azure pipelines: build and unit tests on every pull
  request and on `main`, integration tests, a nightly analysis run, and a release workflow triggered
  by a `v*` tag. The manual approval that guards a nuget.org push is preserved as a GitHub
  environment with a required reviewer — same semantics, different mechanism.
- **BREAKING for the downstream consumer: the internal preview feed is retired.** Today every push
  to `main` publishes a preview build to an Azure Artifacts feed. That feed goes away with no
  replacement; only tagged `v*` releases are published, and only to nuget.org. The consumer that
  relies on the preview channel must be told before the feed stops, not after.
- **The internal working-context directory leaves the repository**, into a private repository that
  holds one such directory per project, linked into each checkout by symlink and ignored by git. The
  same private repository takes the pre-migration OpenSpec archive and the launch-marketing content.

## Capabilities

### New Capabilities

None. This change introduces no capability.

### Modified Capabilities

**`package-distribution`** — two requirements.

*Publication lanes.* The capability guaranteed that every push to the main branch publishes a
pre-release build to the internal feed, and that a tag additionally reaches the public registry
behind an approval. Retiring the feed withdraws the first half outright — the breaking change this
proposal states above. What replaces it says the one thing that is still true: only a version tag
publishes, only with an approval, and nothing at all is published between tags.

*Internal-only material.* The requirement forbidding a published artefact from referencing the
internal working directory stands, but it justified itself by that directory being excluded from the
mirror's allowlist. With no mirror, the reason is now simply that the directory is not in the
repository — and the gate the requirement promises has to exist again, because removing the mirror
took the cleanliness scan with it.

*This delta was missed when the change was written.* The proposal set `skip_specs: true` on the
reasoning that moving a project alters no guarantee, while two sections higher it called the retired
feed breaking for the consumer. Both statements cannot hold. The marker is removed and the delta
lives at `specs/package-distribution/spec.md`.

## Impact

**Superseded**

- `.claude/docs/github-first-migration-option.md` — recorded 2026-08-02 as an option the owner had
  explicitly not commissioned. This change commissions it, and departs from it in two places: the
  working-context directory becomes a directory in the existing `ClaudeProjectContext` repository
  rather than a repository of its own, and OpenSpec was not considered there at all.
- The change `publish-openspec-to-mirror` — its four preconditions are either met (the three
  security fixes shipped in `3.4.0`, and the consumer was coordinated with privately before that
  tag) or absorbed here (the cleanup of internal references, the removal of consumer names from
  archived design notes). Its stated goal is this change's outcome, reached by removing the mirror
  rather than by extending its allowlist. It is archived as part of this work.
- `.claude/rules/public-mirror-cleanliness.md` — the editorial defense that kept internal references
  out of files the mirror would carry. With no mirror and no internal directory in the repository,
  a reference has nowhere to leak from.

*These three paths name the internal directory deliberately. It is the subject of this change, the
rule that discouraged naming it is one of the things being removed, and a reader who finds `.claude`
in `.gitignore` is owed the explanation.*

**Removed**

`scripts/sync-to-github.sh`, `scripts/sync-to-github-ci.sh`, `scripts/check-public-mirror.sh`,
`scripts/trigger-publish.sh`, `azure-pipelines-sync-github.yml`,
`azure-pipelines-unit-tests.yml`, `azure-pipelines-publish.yml`,
`azure-pipelines-integration-tests.yml`, `azure-pipelines-sonarqube.yml`,
`.github/workflows/close-prs.yml`, the cleanliness scan inside `scripts/local-gauntlet.sh`, and the
`marketing/` solution folder in `Stratara.slnx`.

**Rewritten**

`CONTRIBUTING.md` (the contribution path), `README.md` (build badges), the release skills that drive
Azure DevOps through `az repos` and `az pipelines`, and the internal operator documentation for the
publish pipeline.

**Unaffected**

`src/`, `tests/`, `samples/`, `Directory.Build.props`, `Directory.Packages.props`, every `.csproj`,
`Stratara.Publish.slnf`, and every capability in `openspec/specs/` other than `package-distribution`.
No consumer recompiles anything because of this change; the only consumer-visible effect is the
retired preview feed and, from the tag after this ships, a release published from a different system.

**Corrected**

The consistency sweep the move owed and did not pay: the statements about the preview feed, the
internal tracker and the Azure pipelines that survived in `README.md`, `SECURITY.md`, `SUPPORT.md`,
two scripts and the internal reference documentation, each of which now describes a system that was
switched off. Listed individually in `tasks.md` §11.
