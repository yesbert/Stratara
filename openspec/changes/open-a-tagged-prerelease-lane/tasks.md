## 1. Clear the way

- [x] 1.1 Remove task 8.5 from `openspec/changes/make-github-the-only-repository/tasks.md` and record
      in that change's proposal, under Impact, that the release-path proof moved to
      `open-a-tagged-prerelease-lane` because the move never had the means to produce it. Its first
      half — registering the trusted-publishing policy — was done on 2026-08-30 and stays, so the
      task is narrowed rather than deleted and the move reads 55/55. Verify: `openspec list` shows
      the move complete, no remaining task claims a prerelease run, and
      `openspec validate make-github-the-only-repository --strict` passes.
- [x] 1.2 Archive `make-github-the-only-repository`. Verify: `openspec/specs/package-distribution/spec.md`
      contains the requirement *A release is published only from a version tag, and only with
      approval* and no longer mentions an internal feed, and `openspec validate --specs --strict`
      passes.

## 2. Teach the release workflow about prereleases

- [x] 2.1 Split the version rule in `.github/workflows/release.yml` ("Resolve and verify the
      version"): compare only the tag's release part against `<VersionPrefix>`, and expose the
      prerelease identifier as a separate output. A tag whose release part disagrees with the props
      must still fail with the message it fails with today. Verify: locally exercise the rule's shell
      logic over `v4.0.0` (passes, empty suffix), `v4.0.0-preview.1` (passes, suffix `preview.1`),
      `v4.1.0-preview.1` against props `4.0.0` (fails), and `v4.0.0-preview.1` against props `4.1.0`
      (fails).
- [x] 2.2 Pass the resolved identifier to the pack step instead of the hardcoded `/p:VersionSuffix=`,
      so a stable tag still packs stable. Verify: `dotnet pack Stratara.Publish.slnf -c Release
      -p:VersionSuffix=preview.1 -o <tmp>` produces 25 `*.4.0.0-preview.1.nupkg` files, and the same
      command with an empty `VersionSuffix` produces stable ones.
- [x] 2.3 Confirm nothing else in the workflow assumes stability — the artifact upload, the release
      notes and the push step. Verify: `grep -n "VersionSuffix\|preview\|stable" .github/workflows/release.yml`
      returns only the lines this change wrote.

## 3. Correct the documents that deny a preview channel exists

*Each carries one welded sentence: a true clause about tags and a false clause about a channel. The
true half stays everywhere.*

- [x] 3.1 `README.md` (Versioning) and `CONTRIBUTING.md` (Releases). Verify: neither says a preview
      channel does not exist, both still say that what is not tagged is not published.
- [x] 3.2 `SECURITY.md` — the supported-versions note, and the stance reversal: a prerelease is a
      build we invite testing on, so a finding against one is in scope. The out-of-scope line about
      unreleased code between tags is already correct and stays. Verify: a reader can tell whether to
      report a bug found in `4.0.0-preview.1`, and the answer is yes.
- [x] 3.3 `.claude/CLAUDE.md` (the standing "There is no preview channel" under Workflows) and
      `.claude/docs/versioning.md` (the release-model table gains the prerelease row; the section on
      what the loss of the preview lane meant is rewritten around what returned and what did not).
      Verify: the table lists no-bump, prerelease tag and stable tag, and says who decides each.
- [x] 3.4 `.claude/skills/bump-version/SKILL.md` — Step 0's statement that there is no preview
      channel, and Step 1, which assumes it creates a changelog section: it must handle a section
      that already exists and only needs its date. Verify: the skill describes both the "open the
      section" and the "date the section" case, and names which one a prerelease uses.
- [x] 3.5 `.claude/skills/pr/SKILL.md` Step 5. Verify: the follow-up table distinguishes a prerelease
      tag from a stable one.
- [x] 3.6 Sweep for the ones this list missed. Verify:
      `git grep -In -i "no preview channel\|no pre-release channel"` returns nothing outside
      `CHANGELOG.md` history and `openspec/changes/archive/`.

## 4. Open the 4.0.0 cycle

*Owner's call to start, per the standing rule that a bump is never reflexive.*

- [x] 4.1 Bump `<VersionPrefix>` to `4.0.0` — a bump without a release — and rename the changelog's
      `## [Unreleased]` to `## [4.0.0] — unreleased`, leaving it open for further merges. Verify:
      `dotnet pack Stratara.Publish.slnf -c Release -p:VersionSuffix=preview.1 -o <tmp>` produces
      packages whose nuspec `<releaseNotes>` carries the 4.0.0 body, which proves the existing
      extraction pattern tolerates the undated heading.
- [x] 4.2 Confirm the local gate is green on the bumped tree before any tag exists. Verify:
      `./scripts/local-gauntlet.sh --simulate-tag-mode` passes.
      *It did not, at first.* `AiIndexTests.TheStatedStableVersion_MatchesTheLockstepVersion` asserted
      that `llms.txt`'s stated stable version equals `<VersionPrefix>` — an equation this change
      breaks on purpose, because the prefix now names an unreleased version for a whole cycle.
      Writing `4.0.0` into `llms.txt` would have made it advertise a version nobody can install,
      which is the failure that test exists to catch. The test now compares against the newest dated
      changelog section instead, and is renamed `TheStatedStableVersion_MatchesTheNewestRelease`.
      Owner's call, 2026-08-31.

## 5. Prove the release path — adopted from the move's task 8.5

*The owner's to run: it publishes, and a publish cannot be undone.*

- [ ] 5.1 Push `v4.0.0-preview.1` and approve the `nuget-org` deployment. Verify: the run's log shows
      the short-lived-key path at `release.yml:161` rather than the fallback at `:166`; the 25
      packages appear on nuget.org as a prerelease; and `dotnet add package Stratara.Mediator` without
      `--prerelease` still resolves `3.4.0`. **The prerelease stays listed** — it exists to be tested.
- [ ] 5.2 Only after 5.1 has published through the short-lived key: delete the `NUGET_ORG_API_KEY`
      environment secret, the fallback branch at `release.yml:155-177` and the paragraph explaining
      it at `release.yml:11-17`. Verify: `grep -n "NUGET_ORG_API_KEY" .github/workflows/release.yml`
      returns nothing, and the repository's environment secrets no longer list it.
- [ ] 5.3 Record the outcome in `.claude/roadmap/STATE.md`: the release path is proven or it is not,
      and the release-blockers field says which. Verify: no field still describes the release path as
      never exercised.
