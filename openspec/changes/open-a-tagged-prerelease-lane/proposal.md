> **Status:** approved

# Open a tagged prerelease lane

## Why

A major version cannot be tested before it is final. `4.0.0` carries four breaking changes, and the
only way to put them in a consumer's hands today is to publish `4.0.0` itself — at which point the
version number is spent and the decision is irreversible.

The release workflow refuses a prerelease tag. Its version check compares the whole tag against
`<VersionPrefix>`, so `v4.0.0-preview.1` fails against `4.0.0` before anything is built, and the pack
step hardcodes an empty `VersionSuffix` so it could only ever produce a stable package anyway. The
consequence reaches further than it looks: task 8.5 of `make-github-the-only-repository` asks for a
prerelease run to prove trusted publishing, and that task has never been executable. It was not
waiting on anybody — it was waiting on this.

The internal preview feed that was retired on 2026-08-30 is not what returns here, and retiring it
was right. It published on **every merge** — 444 versions, 397 of them previews — which made a
release tag mean nothing, because a consumer got the change either way. What returns is the opposite
of that: a tag is a hand movement, and nothing sets one on its own.

## What Changes

- **A prerelease is cut from a tag, exactly like a release.** `v4.0.0-preview.1` publishes
  `4.0.0-preview.1` to nuget.org. There is no second feed, no schedule and no merge trigger.
- **The version check stops conflating two questions.** `Directory.Build.props` says which version
  the branch is working toward; the tag says which build of it this is. The check compares the tag's
  release part against `<VersionPrefix>` and lets the prerelease suffix through, which is then packed
  as `VersionSuffix`. `<VersionPrefix>4.0.0</VersionPrefix>` stands still across `preview.1`,
  `preview.2`, a later `rc.1` and finally the stable `v4.0.0`.
- **The suffix is any SemVer prerelease identifier**, not a fixed vocabulary. `rc` must not cost a
  second workflow change later.
- **The approval gate applies unchanged.** A prerelease reaches nuget.org only after a required
  reviewer approves, because the push is as irreversible as a stable one and burns the version
  number permanently either way.
- **The changelog section opens early and is dated late.** `## [Unreleased]` becomes
  `## [4.0.0] — unreleased` at the bump and stays open; later merges write into it, every prerelease
  carries the current draft as its release notes, and the stable tag only replaces `unreleased` with
  a date.
- **Task 8.5 moves out of `make-github-the-only-repository` and into this change**, losing the clause
  that had the prerelease unlisted afterwards. See *Impact*.
- **Documentation stops saying there is no preview channel.** Seven places assert it; in each, the
  sentence welds a claim that is now false to one that stays true.

**Not changing, and deliberately so:** nothing publishes on a merge, in any form. No second feed. No
mechanism is invented to make a prerelease "deliberate" — a tag already is one.

## Capabilities

### New Capabilities

None. This change introduces no capability.

### Modified Capabilities

- `package-distribution`: the requirement *A release is published only from a version tag, and only
  with approval* currently ends "There is no pre-release channel: a version that carries no tag is
  obtainable from no feed." The second half is the invariant and survives untouched; the first half
  becomes false the moment a prerelease tag can publish. The requirement gains prerelease semantics —
  published as a prerelease, behind the same approval, and invisible to a consumer who does not ask
  for prereleases.

## Impact

**Sequencing — this change cannot start until the move is archived**

The delta modifies a requirement that `make-github-the-only-repository` introduces and that is not in
the main spec yet, because that change is unarchived. Two open deltas on one requirement collide at
the second archive. The resolution is to finish the move first, and the obstacle to finishing it is
the same task this change adopts:

```
today                          after this change is planned
─────                          ───────────────────────────
move  ──8.5 blocked──▶ ✗       move ──(8.5 removed)──▶ archived
                                        │
                                        ▼
                               this change ──▶ 8.5 as the proof run ──▶ archived
```

**Moved in from `make-github-the-only-repository`**

Task 8.5 — the prerelease run that proves trusted publishing publishes with a short-lived key rather
than falling silently back to `NUGET_ORG_API_KEY`, followed by deleting that secret, the fallback
branch at `release.yml:155-177` and the paragraph at `release.yml:11-17`. Its instruction to unlist
the prerelease afterwards is dropped: that clause assumed a smoke test with no audience, and this one
has one.

**Changed**

`.github/workflows/release.yml` (version resolution and the pack step),
`.claude/skills/bump-version/SKILL.md` (a changelog section that already exists needs dating, not
creating; and the standing statement that there is no preview channel),
`.claude/skills/pr/SKILL.md` (the same statement).

**Corrected**

`README.md`, `CONTRIBUTING.md`, `SECURITY.md`, `.claude/CLAUDE.md`, `.claude/docs/versioning.md` —
each asserts that no preview channel exists. `SECURITY.md` also reverses a stance rather than a fact:
treating prerelease findings as out of scope was right when previews appeared automatically and
nobody had asked for them, and is wrong for a build whose whole purpose is to be tested.

**Unaffected**

`build/PackageReleaseNotes.targets` — its match pattern already tolerates anything after the version
on the heading line, so `## [4.0.0] — unreleased` resolves without a change.
`Directory.Build.props`, `scripts/bump-version.sh`, every `.csproj`, `src/`, `tests/`, `samples/`,
and every capability other than `package-distribution`. No published type or API surface changes, so
no consumer recompiles because of this.
