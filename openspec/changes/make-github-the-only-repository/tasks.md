## 1. Prepare the private context repository

- [x] 1.1 Create `ClaudeProjectContext/Stratara/` and copy the whole `.claude/` tree into it, then
      commit there. Verify: `link.sh` lists `Stratara` among its available projects, and
      `ClaudeProjectContext/Stratara/CLAUDE.md` exists.
- [x] 1.2 Add `scheduled_tasks.lock` to `ClaudeProjectContext/.gitignore`, which today carries only
      `.DS_Store`, `settings.local.json` and `worktrees/`. Verify: `git status` in that repository
      stays clean after the agent has run once against the linked directory.
- [x] 1.3 Confirm `settings.local.json` is present but untracked in `ClaudeProjectContext`. Verify:
      `git ls-files Stratara/settings.local.json` prints nothing while the file exists on disk.

## 2. Move the pre-migration OpenSpec archive out

- [x] 2.1 Move the 24 `2026-08-19-backfill-*` directories and `2026-08-19-migrate-to-openspec`
      (including its `evidence/`) from `openspec/changes/archive/` to
      `ClaudeProjectContext/Stratara/archive/openspec-changes/`, mirroring LoomWeaver's layout.
      Verify: `ls openspec/changes/archive | wc -l` prints 19, and the same count of directories
      appears in the private repository.
- [x] 2.2 Add a `README.md` to `ClaudeProjectContext/Stratara/archive/openspec-changes/` saying what
      these changes are, why they are not public, and that their content lives on in
      `openspec/specs/`. Verify: the file names the date of the backfill and this change.
- [x] 2.3 Check the 19 surviving archived changes and the 3 open ones for references to the moved
      directories. Verify: `grep -rn "backfill-\|migrate-to-openspec" openspec/` returns only matches
      that read as prose, not as a path a reader would try to open.

## 3. Clean the surviving OpenSpec content

- [x] 3.1 Replace every reference to the internal context directory in `openspec/config.yaml` with
      the statement it points at. Verify: `grep -c "\.claude" openspec/config.yaml` prints 0 and the
      file still parses (`openspec validate --all --strict`).
- [x] 3.2 Do the same for the 12 remaining files, in `2026-08-25-resolve-consistency-findings`,
      `2026-08-25-triage-enhancement-backlog`, `2026-08-28-bound-the-outbox-drain-by-progress`,
      `2026-08-28-expire-an-abandoned-replay-marking`,
      `2026-08-28-offer-identity-stores-a-context-per-operation`,
      `2026-08-28-reject-an-ownerless-event-subject`, `2026-08-28-verify-the-documentation-surface`
      and `publish-openspec-to-mirror`. Verify: `grep -rl "\.claude" openspec/` returns nothing.
- [x] 3.3 Remove the consumer application names from the five archived changes that carry them —
      `2026-08-28-bound-the-outbox-drain-by-progress`, `2026-08-28-expire-an-abandoned-replay-marking`,
      `2026-08-28-harden-bus-envelope-against-replay`,
      `2026-08-28-offer-identity-stores-a-context-per-operation` and
      `2026-08-28-reject-an-ownerless-event-subject` — describing the situation instead. Verify: a
      case-insensitive grep over `openspec/` for each consumer application's name returns nothing.
      The names are deliberately not written out here — spelling them into a public file is the
      thing this task removes.
- [x] 3.4 Re-read each rewritten change end to end and confirm the reasoning still stands without the
      attribution. Verify: each one still answers "why was this done" on its own.
- [x] 3.5 Run `openspec validate --all --strict`. Verify: it passes. Note that it does not pass today
      for a reason unrelated to this change — `anchor-event-subject-to-the-stream` has a MODIFIED
      requirement that drops a scenario the current spec still carries. That change is open and its
      own work fixes it; if it is still open at the flip, this task is satisfied when the only
      failure is that one and it is understood.

## 4. Detach the context directory from the repository

- [x] 4.1 `git rm -r --cached .claude` (52 tracked files). Verify: `git ls-files .claude` prints
      nothing.
- [x] 4.2 Replace the three `.claude/...` entries at `.gitignore:448-451` with a single `.claude`
      entry plus a comment saying where the directory lives and that it is linked in — the same
      wording LoomWeaver's `.gitignore` uses. Verify: `git check-ignore -v .claude` names the new
      line.
- [x] 4.3 Delete the real `.claude/` directory and run
      `ClaudeProjectContext/link.sh Stratara /Volumes/Daten/Projects/StrataraV2/Stratara`. Verify:
      `readlink .claude` prints the path into the private repository, and `git status` is clean.
- [x] 4.4 Move `marketing/` to `ClaudeProjectContext/Stratara/marketing/` and remove the
      `marketing/` solution folder from `Stratara.slnx`. Verify: `marketing` appears nowhere in
      `git ls-files`, and `dotnet build Stratara.slnx` still resolves every project.

## 5. Remove the mirror apparatus

- [x] 5.1 Delete `scripts/sync-to-github.sh`, `scripts/sync-to-github-ci.sh`,
      `scripts/check-public-mirror.sh`, `scripts/trigger-publish.sh` and
      `.github/workflows/close-prs.yml`. Verify: `git ls-files scripts .github` no longer lists them.
- [x] 5.2 Remove the public-mirror cleanliness scan from `scripts/local-gauntlet.sh` and delete the
      rule file it enforced. Verify: `./scripts/local-gauntlet.sh` runs green and no step references
      a deleted script.
- [x] 5.3 Delete the five `azure-pipelines-*.yml` files and `sonar-project.properties`' Azure-only
      settings, keeping the analysis configuration the new workflow needs. Verify: no
      `azure-pipelines-*.yml` remains, and `sonar-project.properties` names no Azure task or
      variable.
- [x] 5.4 Grep the tree for what the removed apparatus used to hide, and fix every live dependency
      on it — a script, a test, a build comment, a page of documentation. Verify:
      `grep -rn "dev.azure.com\|azure-pipelines\|sync-to-github\|check-public-mirror" --include='*.md' --include='*.cs' --include='*.yml' --include='*.sh' --include='*.props' --include='*.targets' .`
      returns only historical statements — `CHANGELOG.md` entries and archived changes describing
      what was true when they were written. Nothing that a build, a test or a reader would follow.

## 6. Write the GitHub Actions workflows

- [x] 6.1 Extend `.github/workflows/ci.yml` with a `pull_request` trigger against `main`, keeping its
      `push`, `schedule` and `workflow_dispatch` triggers and its `Stratara.Publish.slnf` scope.
      Verify: a pull request opened against `main` shows the check running.
- [x] 6.2 Add `.github/workflows/integration.yml`, porting the integration suites the Azure
      integration pipeline runs. Verify: the run executes `tests/*.IntegrationTests` and its
      containers start on the hosted runner.
- [x] 6.3 Add `.github/workflows/sonar.yml` on `schedule` at 04:00 UTC plus `workflow_dispatch`,
      carrying the coverage collection the Azure pipeline uses
      (`--coverage --coverage-output-format xml` feeding `sonar.cs.vscoveragexml.reportsPaths`).
      Verify: no `pull_request` or `pull_request_target` trigger appears in the file, and a manual
      run reports non-zero coverage.
- [x] 6.4 Add `.github/workflows/release.yml` on `v*` tags: pack the 25 packable projects, then push
      `.nupkg` and `.snupkg` to nuget.org from a job in the `nuget-org` environment. Include the
      check that the tag matches `Directory.Build.props` `<VersionPrefix>`, modelled on LoomWeaver's
      release workflow. Verify: the version check fails a deliberately mismatched tag on a branch
      before any push step is reached.
- [x] 6.5 Confirm no workflow uses `pull_request_target` and that each declares a minimal
      `permissions` block. Verify: `grep -rn "pull_request_target\|permissions:" .github/workflows/`.

## 7. Verify the tree before the flip

- [x] 7.1 Re-run the reference audit from 3.2, 3.3 and 5.4 over the whole tree, not just
      `openspec/`. Verify: no match for the context directory name, for a consumer application name,
      or for an Azure DevOps URL outside this change's artifacts.
- [x] 7.2 Run `./scripts/local-gauntlet.sh` and `openspec validate --all --strict`. Verify: both pass.
- [x] 7.3 Read the diff of what will become public in one pass. Verify: every file in
      `git ls-files` is something a consumer may read.

## 8. Flip

- [x] 8.1 Push the cleaned tree to `yesbert/Stratara` as one commit on top of the current mirror
      `HEAD`, including `openspec/` and the workflows. **This branch is never merged into the Azure
      DevOps `main`** — its content becomes the first GitHub commit instead. Merging it there would
      delete the Azure pipeline definitions while those pipelines are still the live CI and the
      preview feed still owes the consumer a build, which task 9.1 and 9.2 order deliberately.
      Verify: the commit appears on GitHub and the build workflow goes green on it. This is also
      where the runtime verifications the workflows could not have before they existed are made
      good: 6.1 (a pull request shows the check), 6.2 (containers start), 6.3 (a manual analysis run
      reports non-zero coverage).
- [x] 8.2 Create the `nuget-org` environment with a required reviewer and add `NUGET_ORG_API_KEY` to
      it; add `SONAR_TOKEN` and `SONAR_HOST_URL` as repository secrets. Verify: the environment
      lists the reviewer, and the analysis workflow's manual run reaches the server.
- [x] 8.3 Add a ruleset on `main`: pull request required, the build check required, force-push
      blocked. Verify: a direct push to `main` is rejected.
- [x] 8.4 Clone `yesbert/Stratara` fresh as the working copy and re-run `link.sh` against it. Verify:
      `git remote -v` shows only the GitHub remote, `readlink .claude` resolves, and
      `./scripts/local-gauntlet.sh` passes in the new clone.
- [ ] 8.5 Exercise the release workflow once with a prerelease tag. Verify: the package appears on
      nuget.org as a prerelease, then unlist it.

## 9. Coordinate, then freeze

- [ ] 9.1 Tell the downstream consumer, on the private channel, that the preview feed stops and that
      only tagged releases follow. Verify: the consumer has confirmed, and the confirmation is
      recorded in the project's state file.
- [ ] 9.2 Only after 9.1: disable the five Azure pipelines — Unit Tests, Publish NuGets, SonarQube,
      Integration Tests, Sync GitHub. Verify: each shows as disabled and no run is queued.
- [ ] 9.3 Set the Azure DevOps repository read-only and add a note to its description pointing at
      GitHub. Verify: a push to it is rejected.
- [ ] 9.4 Leave the Azure Artifacts feed in place, unfed, rather than deleting it. Verify: existing
      preview versions still resolve for anyone who pinned one.

## 10. Follow through

- [x] 10.1 Rebuild the `pr` skill against `gh pr` and GitHub checks, dropping `az repos pr`. Verify:
      the skill opens a pull request and waits for the build check.
- [x] 10.2 Rebuild the `publish-nugets` skill against the release workflow, and point `bump-version`
      at the GitHub remote. Verify: neither skill invokes `az`.
- [x] 10.3 Rewrite the internal publish-pipeline operator document for GitHub Actions, and update the
      project's state file: the internal-feed field goes, the pipeline table becomes the workflow
      table. Verify: no internal document describes a pipeline that no longer exists.
- [x] 10.4 Update the context file and core rules: the pipeline table, the Git section, the skill
      table, and the removed cleanliness rule. Verify: every path and command it names resolves.
- [x] 10.5 Rewrite `CONTRIBUTING.md` so a pull request is the contribution path rather than a
      pointless act, and fix the badges in `README.md`. Verify: `CONTRIBUTING.md` contains no
      sentence about a one-way mirror.
- [x] 10.6 Sweep `docs/` for references to the Azure pipelines and the mirror. Verify:
      `grep -rn "Azure DevOps\|azure-pipelines\|mirror" docs/` returns only historically accurate
      statements.
- [x] 10.7 Record the migration in the project's phase history with its date. Verify: the entry says
      what moved, what was frozen, and what a reader has to do to find the old history.

## 11. Close

- [x] 11.1 Archive `publish-openspec-to-mirror`, noting that this change reached its goal by removing
      the mirror. Verify: it is under `openspec/changes/archive/` and `openspec list` no longer shows
      it as open. Its status line stays `proposed` — it was never implemented as written, and marking
      a superseded plan approved after the fact is exactly what the rule against retroactive approval
      forbids.
- [x] 11.2 Confirm the open changes `anchor-event-subject-to-the-stream` and
      `require-an-explicit-tenant-selection` came through the move intact. Verify:
      `openspec validate --all --strict` passes and both still read as proposed.
