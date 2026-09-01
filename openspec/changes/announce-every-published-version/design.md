## Context

See `proposal.md` — *Why*. What matters here is the shape of the workflow the change extends.

`.github/workflows/release.yml` runs on `push: tags: ['v*']` with a top-level `permissions:
contents: read`, and splits into two jobs along one line: which of them holds a credential.

- **`pack`** — checkout, build, test, pack, upload the nupkgs as an artifact. No credential in
  scope. It also resolves the version: the tag's release part (everything before the first hyphen)
  must equal `<VersionPrefix>` in `Directory.Build.props`, and any prerelease identifier after that
  hyphen rides through to pack as `VersionSuffix`.
- **`publish`** — `needs: pack`, `environment: nuget-org` (a required reviewer), `id-token: write`
  for the trusted-publishing token exchange, then a push loop with `--skip-duplicate`.

Nothing creates a release entry in the repository. The 3.x entries were written by hand.

Two existing pieces the design leans on:

- `build/PackageReleaseNotes.targets` extracts `<PackageReleaseNotes>` from `CHANGELOG.md` by
  matching `^##\s*\[<version>\][^\n]*\n` and capturing to the next `## [` heading or end of file.
  It keys off `$(VersionPrefix)` — the release part — so a `4.0.0-preview.1` package already takes
  its notes from the `## [4.0.0]` section. Missing section means empty notes and a green build.
- `/bump-version` opens `## [<version>] — unreleased` at the bump and the stable tag only replaces
  `unreleased` with a date, so the section exists before any tag for that version is pushed.

## Goals / Non-Goals

**Goals:**

- The repository's release list is a consequence of publishing, not a habit.
- The permission needed to write that list never shares a job with the nuget.org key.
- Package notes and repository notes come from the same changelog section, for the same version.

**Non-Goals:**

- Changing what publishes, when, or on whose approval. The gate is untouched.
- Attaching the `.nupkg` files to the release entry. The registry is where packages are obtained;
  a second copy on the release entry is a second thing that can disagree.
- Generating notes from commits or pull requests. The changelog is the single source and stays it.
- Backfilling the release list further than `v4.0.0`.

## Decisions

### A third job, not a step in `publish`

`announce` is its own job with `needs: publish` and `permissions: contents: write`.

*Alternative rejected:* a step at the end of `publish`. It is fewer lines, and it would put a
`contents: write` token in the same job as the short-lived nuget.org key and the packages about to
be pushed. The workflow's existing split is precisely this argument applied once already — `pack`
holds no credential so that a build-time compromise cannot reach nuget.org. Widening `publish`
instead of extending the split spends that reasoning to save six lines of YAML.

`needs: publish` also gets the ordering and the gate for free: `announce` cannot start until the
required reviewer has approved and the push has succeeded, so a declined approval announces nothing,
and a failed push announces nothing. No second `environment:` is needed — the gate is inherited by
depending on the job that carries it.

### Notes extracted again, in bash, against the same heading

The announce job reads the `## [<release-part>]` section out of `CHANGELOG.md` itself rather than
receiving it from `pack`.

*Alternatives rejected:*

- **Have `pack` emit the notes as an artifact and download them.** One extractor, byte-identical
  output — but getting the value out of MSBuild means either `-getProperty` combined with running a
  target that is wired `BeforeTargets="GenerateNuspec"`, or a second target written to dump the
  property to a file. Both are more moving parts inside the packing build, which is the job that
  must not become fragile, to avoid five lines of `awk` in a job that has nothing else to do.
- **`gh release create --generate-notes`.** Free, and it lists merged pull requests instead of the
  changelog — which contradicts *Release notes come from the changelog* and would make the two
  announcements of the same version disagree.

So the pattern is implemented twice, in C# and in bash. This is deliberate duplication of a
five-line regex across two languages, not an oversight: a task adds a comment in each pointing at
the other, so a later reader finds the pair rather than deleting one as redundant.

The lookup key is the **release part** of the tag, matching what `PackageReleaseNotes.targets` keys
off. `v4.1.0-preview.1` therefore announces the `## [4.1.0]` section — the same text its packages
carry — rather than looking for a section per prerelease, which the bump workflow never creates.

### `gh` from the runner, no third-party action

`gh` is preinstalled on `ubuntu-latest` and authenticates from `GITHUB_TOKEN`. Three lines of
`gh release create` need no third-party action reviewed, pinned and re-reviewed — and a marketplace
action here would be one holding a `contents: write` token.

### Idempotent by checking, not by overwriting

The job asks whether an announcement for the tag already exists and does nothing if it does.

*Alternative rejected:* create-or-edit, so a re-run refreshes the notes. A maintainer may have
edited an entry by hand after the fact — an upgrade note, a correction — and an overwrite on re-run
discards it silently. Not-touching is the safer direction for something already published, and it
matches `--skip-duplicate` on the push loop, which also declines to overwrite what is there.

### Prerelease detection from the tag alone

A tag containing a hyphen is a prerelease and is announced with `--prerelease`. The version check in
`pack` already treats the first hyphen as the boundary between the release part and the prerelease
identifier; using the same rule keeps one definition of "is this a prerelease" in the workflow
rather than two that can drift.

### Title matches the existing entries

`Stratara <version>` — what the 3.x entries use, so the list stays readable as one series rather
than showing where the mechanism took over.

## Risks / Trade-offs

- **`announce` fails after the packages are published.** → The packages are out and correct; only
  the repository is behind, which is exactly today's state and no worse. The job is idempotent and
  re-runnable on its own, and the failure is visible on the run rather than silent.
- **The two extractors drift.** → Both key off the same heading form, which is fixed by the
  Keep-a-Changelog format the file already follows and by `/bump-version` writing it. Cross-comments
  in both files make the pairing discoverable. The failure mode is also benign and visible: notes
  differ between the package and the release entry, rather than a release breaking.
- **`contents: write` now exists somewhere in the release workflow.** → Confined to a job that holds
  no other credential, runs only after a human approval, and does nothing but read the changelog and
  create one release entry.
- **A version published before this change stays unannounced.** → Only `v4.0.0` is in that state,
  and the migration below closes it. `v4.0.0-preview.1` is left alone on purpose: a prerelease entry
  does not change which version the repository presents as current.

## Migration Plan

1. Merge the workflow change. Nothing happens on merge — the workflow runs on tags only.
2. Announce `v4.0.0` by hand, once, with the `## [4.0.0] — 2026-08-31` section as its notes and the
   title `Stratara 4.0.0`. This is an outward-facing action on a public repository and waits for the
   owner's explicit go, separately from approving this change.
3. The next tag — stable or prerelease — is the first end-to-end proof. No dry run is possible: the
   job runs behind the publish gate by design, so exercising it means publishing something.

**Rollback:** delete the `announce` job. It writes nothing the rest of the workflow reads, and no
package, tag or feed depends on it.
