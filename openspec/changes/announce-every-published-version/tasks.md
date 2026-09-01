## 1. The announce job

- [x] 1.1 Add job `announce` to `.github/workflows/release.yml` — `needs: publish`,
  `runs-on: ubuntu-latest`, `timeout-minutes: 10`, `permissions: contents: write`, and a checkout at
  the tag so `CHANGELOG.md` is present. Verify: the job appears in the workflow's job graph on the
  next run, after `publish`.
- [x] 1.2 Resolve the release part of the tag in the job (everything before the first hyphen), using
  the same boundary the version check in `pack` uses at `release.yml` — *Resolve and verify the
  version*. Verify: a step echoes the release part, and for `v4.0.0-preview.1` it is `4.0.0`.
- [x] 1.3 Extract the `## [<release-part>]` section from `CHANGELOG.md` to a notes file, matching the
  heading form `build/PackageReleaseNotes.targets` matches — heading line, body up to the next
  `## [` heading or end of file. Verify: run the extraction locally against the committed
  `CHANGELOG.md` for `4.0.0` and diff it against the `## [4.0.0]` section.
- [x] 1.4 Create the release entry with `gh release create` — title `Stratara <version>`,
  `--notes-file` from 1.3, `--verify-tag`, and `--prerelease` when the tag contains a hyphen.
  Verify: the entry created by the next tagged run carries the changelog body and the correct
  prerelease flag.
- [x] 1.5 Degrade instead of failing when the changelog has no section for the version: announce with
  no notes and log why. Covers the spec scenario *The changelog has no section for the version*.
  Verify: the extraction step against a version absent from `CHANGELOG.md` produces an empty notes
  file and a zero exit code.
- [x] 1.6 Make the job idempotent by checking for an existing entry with `gh release view` and
  skipping creation if one exists — never editing it. Covers the spec scenario *The same version is
  published a second time*. Verify: re-running the workflow for an already-announced tag succeeds
  and leaves the entry byte-identical.
- [x] 1.7 Add the cross-reference comments the design calls for: in `build/PackageReleaseNotes.targets`
  and at the extraction step in `release.yml`, each naming the other as the second implementation of
  the same heading match. Verify: both comments exist and name the other file by path.
- [x] 1.8 Add the job's header comment in the same voice as the `pack` and `publish` comments — why
  it is a separate job rather than a step in `publish` (the `contents: write` token never shares a
  job with the nuget.org key), and that it inherits the approval gate through `needs: publish`.

## 2. Documentation that describes the pipeline

- [x] 2.1 Update `.claude/docs/nuget-publish-pipeline.md` — it documents *Job `pack`* and *Job
  `publish`* and now misses one. Add `announce` with its permission boundary, and add the failure
  mode "the packages published but the release entry is missing" to the troubleshooting list next to
  the `--skip-duplicate` entry. Verify: the document's job list matches the workflow's.
- [x] 2.2 Update `.claude/skills/publish-nugets/SKILL.md` — the skill takes a release from its tag to
  nuget.org and stops at the registry. Verifying it landed now includes confirming the release entry
  exists for the tag and is flagged correctly. Verify: the skill's verification steps name the
  release entry.
- [x] 2.3 Add a `CHANGELOG.md` entry under `## [Unreleased]`. Verify: the section names the workflow
  change.

## 3. Close the gap this change was opened for

- [x] 3.1 **Owner's go required, separately from approving this change.** Announce `v4.0.0` by hand:
  title `Stratara 4.0.0`, notes from the `## [4.0.0] — 2026-08-31` section, not a prerelease.
  Verify: `gh release list` shows `Stratara 4.0.0` as *Latest*. `v4.0.0-preview.1` stays
  unannounced on purpose.

## 4. Verification

- [x] 4.1 Run `./scripts/local-gauntlet.sh`. Verify: green. The change touches no compiled code, so
  this is a guard against the documentation and changelog gates, not the build.
- [x] 4.2 Run `openspec validate announce-every-published-version --strict`. Verify: no findings.
