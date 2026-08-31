# Design — Open a tagged prerelease lane

## Context

See `proposal.md` → *Why*. What matters for the approach is the shape of what exists today.

The release workflow holds one invariant, stated in its own comment: *"The tag is the version, and
the working tree has to agree with it."* The reason is sound — a tag `v4.1.0` pushed onto a tree that
says `<VersionPrefix>4.0.0</VersionPrefix>` would be a guess rather than a decision. The
implementation is stricter than the reason:

```
tag="${GITHUB_REF#refs/tags/v}"                     →  "4.0.0-preview.1"
props=<VersionPrefix> from Directory.Build.props    →  "4.0.0"
[[ "${tag}" != "${props}" ]] && exit 1              →  fails
```

Two independent statements are compared as one string. Downstream, the pack step writes
`/p:VersionSuffix=` unconditionally, so even a tag that passed the check could only produce a stable
package.

Three things the surrounding machinery already gets right, and which the design leans on rather than
rebuilds:

- `on: push: tags: ['v*']` matches a prerelease tag today. No trigger change.
- The publish job sits in the `nuget-org` environment behind a required reviewer, and the pack job
  holds no credential at all.
- `build/PackageReleaseNotes.targets` matches `^##\s*\[<version>\][^\n]*` — the `[^\n]*` swallows
  whatever follows the closing bracket, so a heading dated or marked any way at all still resolves.

## Goals / Non-Goals

**Goals:**

- A prerelease is cut by the same act as a release: pushing a tag, and approving the push.
- The branch keeps stating one version for the whole cycle toward it, whatever prereleases happen on
  the way.
- The prerelease identifier is free-form, so a later `rc` costs nothing.
- The first prerelease tag is safe to attempt: a wrong version rule fails before any credential is in
  scope.

**Non-Goals:**

- Any publication that is not a tag push. This is the design's load-bearing exclusion, not a
  simplification — see *The deliberateness is structural*.
- A second feed, a second registry, or a channel concept beyond what SemVer and nuget.org already
  express.
- Automating *when* a prerelease is warranted. That judgement stays with the owner.
- Changing what a stable release does. A tag with no prerelease identifier behaves exactly as it does
  today.

## Decisions

### The props state the destination; the tag states the stop

The check compares the tag's release part against `<VersionPrefix>` and treats the prerelease
identifier as free. The identifier is then passed to pack as `VersionSuffix`.

```
<VersionPrefix>4.0.0</VersionPrefix>   ← unchanged across the whole cycle
       ├── v4.0.0-preview.1  →  VersionSuffix=preview.1  →  4.0.0-preview.1
       ├── v4.0.0-preview.2  →  VersionSuffix=preview.2  →  4.0.0-preview.2
       ├── v4.0.0-rc.1       →  VersionSuffix=rc.1       →  4.0.0-rc.1
       └── v4.0.0            →  VersionSuffix=           →  4.0.0
```

The invariant survives on the half that carries it: tagging `v4.1.0-preview.1` against a `4.0.0` tree
still fails, which is the drift the check was written to catch. Only the comparison that was never
protecting anything is relaxed.

*Rejected: carry the suffix in `Directory.Build.props` as well.* It restores a single string to
compare, at the cost of editing the props file for every prerelease and remembering to empty it
before the stable tag. Forget once and the stable package ships named `4.0.0-preview.3` — a failure
that is unrecoverable, because a published version cannot be replaced. A design whose correctness
depends on remembering to undo something is worse than the check it fixes.

*Rejected: derive the whole version from the tag and stop reading the props.* It removes the drift
check entirely, which is the one thing standing between a typo in a tag and a wrong version on
nuget.org.

### The deliberateness is structural, so nothing enforces it

The retired feed failed because publication was a consequence of merging. Nothing here makes a
prerelease automatic: there is no schedule, no branch trigger, and a tag does not create itself. The
approval gate sits behind it as well.

No cadence rule, quota or naming policy is added to make prereleases "rare enough". A rule that
constrains a manual act adds ceremony without adding a guarantee, and the failure it would guard
against — hundreds of unasked-for versions — was a property of automation, not of human frequency.

*Consequence:* if prereleases do turn out to proliferate, that is information about how the project
releases, not a hole in this design.

### The approval gate covers prereleases too

Owner decision, 2026-08-31. The reasoning is that the two cases are identical where it counts: a
push to nuget.org cannot be undone, only unlisted, and the version identifier is spent forever
either way. A prerelease is not a lesser act than a release; it is a smaller audience.

*Trade-off, accepted:* the owner is the reviewer, so the gate is self-approval. Its value is the
pause, not the second party.

### The changelog section opens early and is dated late

`## [Unreleased]` becomes `## [4.0.0] — unreleased` at the bump and stays open. Later merges write
into it; each prerelease packs whatever it currently says; the stable tag replaces `unreleased` with
a date.

This needs no change to the extraction, because the pattern already ignores everything after the
closing bracket. It also makes the changelog honest in a way it is not today: the section is a draft
for exactly as long as the version is a draft.

*Rejected: leave `[Unreleased]` in place and let prereleases ship without release notes.* The
extraction tolerates it — the package would simply carry none. But a prerelease published so somebody
can test it is the build that most needs to say what changed.

*Rejected: teach the extraction to fall back to `[Unreleased]` for prerelease packs.* Same outcome
through more machinery, and it leaves two headings meaning the same thing at the same time.

### Prerelease identifiers are ordered by SemVer, and the dot is load-bearing

The identifier is not constrained to a vocabulary, so `rc` needs no second change. Two ordering
facts the tasks have to respect when they document this:

- A dot-separated numeric identifier is compared numerically: `preview.10` sorts after `preview.9`.
  Written without the dot they are single alphanumeric identifiers compared as strings, and
  `preview10` sorts *before* `preview9`.
- Every prerelease of a version sorts before that version, and a consumer resolving without asking
  for prereleases never sees one.

### Task 8.5 moves rather than staying behind

See `proposal.md` → *Impact* for the sequencing this resolves. The design point is why moving is
correct rather than convenient: 8.5 asks for something the move never had the means to produce, so
leaving it in that change would keep an implemented change in the queue on the strength of a task it
cannot close. The instruction to unlist the prerelease afterwards goes with the move, because it
assumed a build nobody wanted; here the build is the point.

## Risks / Trade-offs

**The first prerelease tag exercises two never-run paths at once — the new version rule and trusted
publishing.** → The pack job runs with no credential in scope: checkout, resolve the version, build,
test, pack. Only the publish job enters the `nuget-org` environment. A wrong version rule therefore
fails before anything could reach nuget.org, which separates the two unknowns in practice even though
one tag triggers both.

**A prerelease identity is spent the moment it is published.** → There is no fixing `preview.1` in
place; a mistake costs `preview.2`. This is inherent to nuget.org rather than to this design, and it
is the reason the approval gate stays.

**A consumer pins a prerelease and is then surprised when it is superseded.** → The requirement
states that a prerelease is not offered to anyone who does not ask for it, so reaching one is a
deliberate act on the consumer's side too. `SECURITY.md` says the rest: a prerelease is a build to
test, not to run in production.

**The documentation is corrected in seven places and one is missed.** → Every one of them carries the
same welded sentence — a true clause about tags and a false clause about a channel — so the sweep is
a single grep for the false half rather than seven separate judgements. The task states the grep.

**Reintroducing the word "preview" invites the old reflex of publishing on merge.** → The spec
forbids it in the requirement text itself rather than in a comment: neither a merge nor a push to the
main branch publishes anything. A future change that wanted to restore automatic publication would
have to modify a requirement, which is a visible act.

## Migration Plan

Ordered, because the first two steps unblock the rest:

1. Remove task 8.5 from `make-github-the-only-repository` and archive that change, folding its
   `package-distribution` delta into the main spec. This change's delta builds on it.
2. Teach `release.yml` the split version rule and pack the suffix through.
3. Correct the documentation and the two skills.
4. Bump to `4.0.0` — a bump without a release — and open the changelog section as
   `## [4.0.0] — unreleased`.
5. Tag `v4.0.0-preview.1`. This is the adopted 8.5: read the publish log for the short-lived key
   rather than the fallback, then delete `NUGET_ORG_API_KEY`, the fallback branch at
   `release.yml:155-177` and the paragraph at `release.yml:11-17`.

*Rollback:* steps 2 and 3 are ordinary reverts. Step 4 is a props edit. Step 5 cannot be rolled back
— a published prerelease can only be unlisted — which is why it is last and why it is the owner's.
