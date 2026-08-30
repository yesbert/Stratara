## Context

See `proposal.md` — *Why*. What follows is the state the plan has to work with.

**The history cannot be republished.** The Azure DevOps repository holds 1,975 commits, and every
one of them carries the internal working-context directory. There is no filter that makes that
history public without rewriting all of it, and a rewrite would break every commit hash referenced
in the changelog, in issues and in the archive.

**The public repository already has a usable history.** `yesbert/Stratara` carries one squashed
commit per merged pull request since 3.2.1, plus a release commit and a GitHub Release per tag. It
is coarse, but it is real history with working tags, and it is what consumers already reference.

**Two repositories, one migration.** Part of the work lands in Stratara and part in
`ClaudeProjectContext`, a private repository that already holds LoomWeaver's context directory in
exactly this shape. OpenSpec's edit scope covers Stratara only, so the steps that write to the other
repository are ordinary work, tracked here but not editing this tree.

**Precedent exists.** LoomWeaver completed the same migration on 2026-08-28: symlinked context
directory, pre-migration OpenSpec archive moved out, Azure DevOps frozen, GitHub Actions for build
and release. Its `link.sh`, its `.gitignore` entry and its release workflow are the reference
implementations, not inventions of this change.

## Goals / Non-Goals

**Goals:**

- One repository, one history, one CI system, from a single cut-over date.
- The published specifications and the reasoning behind them are readable by a consumer.
- The approval that stands between a build and nuget.org survives the move unchanged in meaning.
- Every step before the freeze is reversible; the irreversible ones happen last and in a fixed order.

**Non-Goals:**

- Preserving the granular Azure DevOps history in public. It stays private and archived.
- Rewriting the public history that already exists. This change appends to it.
- Improving the CI beyond a faithful port. A pipeline that is red today is not fixed here, and a
  check that does not exist today is not added.
- Deciding the next version number. The first release through the new path needs that decision, and
  it is the owner's, made separately.

## Decisions

### Fresh cut, not a history rewrite

GitHub continues from the current mirror `HEAD`. The pre-migration Azure DevOps history stays where
it is, read-only.

*Alternatives rejected.* Pushing the full history publishes the internal directory in 1,975 commits.
Rewriting it with `git filter-repo` would strip the directory but invalidate every commit hash the
changelog and the archive cite, and it would still leak through commit messages written for an
internal audience. Starting an empty repository throws away tags consumers already depend on.

*Consequence to accept:* after the cut, `git log` in the working clone shows the mirror's history,
not the internal one. The granular record of how Stratara was built up to 3.4.0 is reachable only in
the frozen Azure DevOps repository.

*Evidence:* the commit counts (1,975 internal against the mirror's per-PR history), and the allowlist
in `scripts/sync-to-github.sh`, which enumerates what was ever public.

### The context directory is a symlink into a shared private repository

`.claude` becomes a symlink to `ClaudeProjectContext/Stratara`, and `.gitignore` carries `.claude`.

*Alternatives rejected.* A repository per project multiplies clones and drifts. A git submodule
tracks a pointer commit in the public repository — the very leak this avoids — and adds a checkout
step for a directory that is not part of the project. A local-only directory outside version control
loses the multi-machine sync that motivates the move.

*Consequences to accept:* the symlink is absolute, so it differs per machine and is deliberately not
tracked; `link.sh` runs once per machine. Checking out a pre-migration commit replaces the symlink
with the real files, and git does not restore it on the way back — the link has to be re-created by
hand. This failure is loud rather than subtle: the agent reports that the project has no
instructions.

*Evidence:* `ClaudeProjectContext/link.sh` and its `README.md`, both in use for LoomWeaver since
2026-08-27.

### The pre-migration OpenSpec archive stays private; everything after it is published

25 of 44 archived changes move out: the 24 capability backfills of 2026-08-19 and the OpenSpec
migration itself, including its evidence directory. 19 archived changes and both open ones are
published.

The backfills recorded behaviour that already existed at the time, in order to derive the
specifications from it. Their content survives — in `openspec/specs/`, which is the whole point of a
backfill. What they add on top is a description of the internal document each requirement came from,
written for a reader who has those documents. For a consumer that is provenance for sources they
cannot open.

This line also resolves two of the four preconditions that `publish-openspec-to-mirror` set. All
five design notes that name consumer applications are backfills, so the naming leaves with them; and
62 of the 75 files that reference the internal directory go with them, leaving 12 files plus
`openspec/config.yaml` to edit by hand.

*Alternatives rejected.* Publishing the backfills means either publishing consumer-internal defect
histories or rewriting 25 changes that nobody will read. Deleting them destroys the derivation of
220 requirements, which is exactly the audit trail a backfill exists to leave.

*Evidence:* `publish-openspec-to-mirror/proposal.md`, preconditions 1–4; the file counts above,
measured by grep over `openspec/`.

### An internal reference is replaced by what it says, not by another link

The 12 remaining files get the statement they point at, inlined. This is the option the removed
cleanliness rule itself recommended first, and it is the only one that survives the deletion of both
the rule and the directory.

### Consumer names go, the reasoning stays

Five archived changes that ship name a consumer application. Each is rewritten to describe the
situation — what was observed, what it implied — without the attribution. The reasoning is what has
value in an archive; the name of who hit the bug is what a security policy promises to withhold.

*Evidence:* `SECURITY.md`, which promises coordination on a private channel before public
disclosure, and the repository rule that Stratara never references a consumer application.

### The approval gate becomes a GitHub environment, and only that one

The nuget.org push runs in an environment with a required reviewer. Everything else — build, tests,
analysis — runs ungated.

*Alternative rejected.* Trusted publishing over OIDC gives provenance attestation and removes the
stored API key, and it is what LoomWeaver uses for npm. NuGet supports it, but it must be configured
per package on nuget.org against a named repository and workflow. With 25 packages that is 25
configurations, and it is a separate change once the workflow it names exists and has run.

*Consequence:* `NUGET_ORG_API_KEY` remains a stored secret, scoped to the environment rather than the
repository.

### Analysis never runs on a fork's pull request

The analysis workflow triggers on `push` and `schedule` only. Not `pull_request`, and under no
circumstance `pull_request_target`.

The analysis server is private and reachable over the internet behind a token. A `pull_request` run
on a fork gets a read-only token and no secrets, so it would fail rather than leak — but
`pull_request_target` runs a stranger's code in this repository's context with access to secrets.
The token that reaches the analysis server is exactly the kind of secret that makes that fatal.

### Big bang, not a hybrid

The repository flip and all four workflows land together. The alternative — pointing the existing
Azure pipelines at the GitHub repository and porting them one at a time — was considered and
rejected by the owner: it stretches a two-system state over weeks and leaves the freeze indefinitely
pending.

*Consequence:* the release path's push step is first exercised for real on the first tag. Mitigated
under *Risks*.

### The preview feed is retired with no replacement

Owner decision. Only `v*` tags publish, and only to nuget.org.

*Consequence:* the downstream consumer loses a channel it uses today. This is the one step in the
plan that breaks something outside this repository, which is why it is ordered explicitly: the
consumer is coordinated with, and confirms, **before** the publishing pipeline is switched off.

## Risks / Trade-offs

**The consumer's build breaks without warning when the preview feed stops.** → The coordination step
is a task with a checkbox, placed before the pipeline is disabled, not after the flip. Until the
consumer confirms, the old publishing pipeline keeps running.

**The release workflow's push step is unproven when the first real release runs.** → Exercise it
once with a prerelease tag before the first stable one. A prerelease on nuget.org is not offered to
consumers resolving stable versions and can be unlisted, so a failed attempt costs a version number
rather than a release. Everything before the push — restore, build, test, pack across all 25
projects — is already covered by the build workflow on every pull request.

**Something is discovered to be missing after Azure DevOps is frozen.** → The freeze is the last
step, and it is reversible: it is a permission setting and five disabled pipeline definitions, not a
deletion. Nothing is deleted from Azure DevOps by this change.

**The working clone and the public repository have unrelated histories.** → The clone is replaced
rather than re-pointed. Re-pointing a remote across unrelated histories produces a state where a
push either fails or, forced, destroys the public history.

**An internal reference or a consumer name survives the cleanup and becomes public.** → The audit is
a task with a stated method (`grep` over `openspec/` for the directory name and for each consumer
name), run again immediately before the flip rather than only at the start of the work.

**The analysis server is unreachable from GitHub-hosted runners.** → It is reachable today: every
current pipeline runs on Microsoft-hosted agents outside any private network. If that turns out to be
wrong, the analysis workflow is the one piece that can be deferred without blocking the migration —
it is a nightly report, not a gate.

## Migration Plan

Three phases, in this order. Everything in phase 0 is reversible and happens in the Azure DevOps
repository; phase 1 contains the irreversible steps; phase 2 follows the flip.

0. Clean the tree: move the pre-migration archive and the marketing content out, resolve internal
   references, remove consumer names, delete the mirror apparatus, write the four workflows.
1. Flip: push the cleaned tree as one commit, configure the repository, replace the working clone,
   coordinate with the consumer, then freeze Azure DevOps.
2. Follow through: rebuild the release skills against GitHub, rewrite the operator and contributor
   documentation, archive `publish-openspec-to-mirror`.

`tasks.md` carries the steps.
