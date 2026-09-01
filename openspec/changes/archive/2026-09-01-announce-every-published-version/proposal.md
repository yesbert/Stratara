> **Status:** approved

# Announce every published version

## Why

Stratara 4.0.0 is on nuget.org. Anyone who opens the repository is told the current version is
3.4.0.

Both statements are true, and that is the problem. The `v4.0.0` tag was pushed on 2026-08-31, the
release workflow succeeded, all 25 packages went to nuget.org — and the repository's release list
still ends at *Stratara 3.4.0*, dated 2026-08-28 and still marked **Latest**. `release.yml` has
exactly two jobs, `pack` and `publish`. Neither announces anything. The 3.x entries that make the
list look maintained were each written by hand, so the list has always been a habit rather than a
mechanism, and 4.0.0 is simply the first time the habit was not performed.

The consequence is not cosmetic. A version is announced in two places a consumer reads — the
registry and the repository — and only one of them is wired to the tag. The one that is not wired is
the one a consumer reaches first, from a search result or a link, before they have a package manager
open. Fixing 4.0.0 by hand restores the answer and leaves the mechanism exactly as it was: the next
release fails the same way, silently, and nobody notices until somebody asks again.

## What Changes

- **A published version announces itself.** After the packages reach the registry, the release
  workflow creates the repository's release entry for that tag. No hand step, and nothing to
  remember after a tag is pushed.
- **The announcement is a third job, not a step in `publish`.** `publish` holds the short-lived
  nuget.org key and runs with `contents: read`. Writing a release entry needs `contents: write`. The
  workflow already separates `pack` (no credential in scope) from `publish` (credential in scope);
  this extends the same cut rather than widening the job that holds the key.
- **It runs after the approval gate, not beside it.** `announce` needs `publish`, so a reviewer who
  declines produces no packages *and* no announcement. Nothing is announced that was not published.
- **The notes are the changelog section for the version** — the same section
  `build/PackageReleaseNotes.targets` already extracts into `<PackageReleaseNotes>` at pack time. The
  changelog stays the only place release notes are written, and the package and the repository say
  the same thing about the same version.
- **A prerelease tag is announced as a prerelease**, so `v4.1.0-preview.1` does not displace 4.0.0
  as the version the repository presents as current. This mirrors what the registry already does for
  a consumer who resolves without asking for prereleases.
- **Two degradations, both matching behaviour that already exists.** A version the changelog does not
  document is announced without notes rather than failing the release — the pack side already
  degrades exactly this way. And re-running a tag whose announcement exists succeeds instead of
  failing on the collision, matching `--skip-duplicate` on the push loop.
- **`v4.0.0` is announced by hand, once.** The workflow fires on a tag push; it cannot reach back to
  one pushed a day ago. `v4.0.0-preview.1` is deliberately left alone — a prerelease entry does not
  affect which version the repository presents as current, and the chronicle loses nothing.

**Not changing, and deliberately so:** what publishes, when, and on whose approval. A tag is still
the only thing that publishes, a merge still publishes nothing, and the required reviewer still
stands between a tag and the registry. This change adds a consequence to a successful publish; it
does not add a way to publish.

## Capabilities

### New Capabilities

None. This change introduces no capability.

### Modified Capabilities

- `package-distribution`: gains a requirement that a published version is announced in the source
  repository, with the changelog section as its notes and a prerelease marked as one. It sits
  between the two requirements it borrows from — *Release notes come from the changelog*, which
  already makes the changelog the single source and which the announcement now reads from too, and
  *A release is published only from a version tag, and only with approval*, whose gate the
  announcement inherits by running behind it. Neither of those two is modified.

## Impact

**Changed**

`.github/workflows/release.yml` — a third job, `announce`, needing `publish`, with
`contents: write`. Nothing in `pack` or `publish` changes.

`build/PackageReleaseNotes.targets` — its match is unchanged; it gains a comment naming the awk
extractor in `release.yml` as the second implementation of the same heading match, so the pair is
discoverable from either end.

`CHANGELOG.md` — an `[Unreleased]` entry.

**Corrected**

`.claude/docs/nuget-publish-pipeline.md` — describes the pipeline as `pack` and `publish` and now
misses a job. The same document's "Environment secret — temporary" section told the reader to delete
`NUGET_ORG_API_KEY` and the fallback branch in `release.yml` after trusted publishing had published
once; both were already gone, so the instruction described work that no longer existed. It now
states the fact instead: there is no nuget.org secret anywhere.

`.claude/skills/publish-nugets/SKILL.md` — walks a release from tag to nuget.org and stops at the
registry; verifying it landed now includes the announcement.

`docs/overview/what-is-stratara.md` — said the changelog is where release notes live. It now also
names the releases page and states that a prerelease does not displace the latest stable version.

**Done once, outside the workflow**

The release entry for `v4.0.0`, with the `## [4.0.0] — 2026-08-31` changelog section as its notes.
This is an outward-facing action on a public repository and needs the owner's go before it happens,
separately from approving this change.

**Unaffected**

`Directory.Build.props`, `scripts/bump-version.sh`, `Stratara.Publish.slnf`, every `.csproj`,
`src/`, `tests/`, `samples/`, and every capability other than `package-distribution`. No published
type or API surface changes, so no consumer recompiles because of this.
