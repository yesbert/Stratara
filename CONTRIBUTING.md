# Contributing to Stratara

Thanks for your interest in Stratara.

## Where the project lives

This repository is the project. Development happens here in the open: branches, pull requests,
issues and releases. It was previously mirrored from a private repository one squashed commit per
release, which is why the history before 2026-08-30 is coarse. Everything after it is not.

## What we welcome

| Type | How |
|---|---|
| **Bug reports** | Open an issue with the `bug` template. The more reproducible, the better — include the Stratara version, the .NET version, and a minimal repro if you can. |
| **Pull requests** | Yes, and see below. Small and focused beats large and sweeping. |
| **Questions** | Open an issue with the `question` template. Please check <https://docs.stratara.tech> first. |
| **Security issues** | Follow [`SECURITY.md`](SECURITY.md) — do **not** file a public issue. |
| **Documentation feedback** | An issue is fine, a pull request is better. The docs live in `docs/` and ship on the same cadence as the code; every hand-written page on the site carries an **Edit this page** link that lands on its source file. The API reference has none — it is generated from the XML documentation comments in `src/`, so it is fixed there. |

Feature requests are the one exception: please **do not file them** yet. The focus is on stabilising
the surface that exists, and an issue proposing a new one will be closed with a pointer to this
paragraph. A bug — something the framework does that its specification says it should not — is
always welcome, and is a different thing.

## Opening a pull request

1. **Fork the repository, then branch from `main`**, prefixed by what the change is: `feature/`,
   `fix/`, `refactor/`, `chore/` or `docs/`. A ruleset rejects a direct push to `main`, and only
   maintainers can push a branch to this repository at all — everyone else opens the pull request
   from their fork.
2. **Run the local gate before you push**: `./scripts/local-gauntlet.sh`. It builds the full
   solution, runs every unit test project, builds the documentation site with warnings as errors,
   and packs every package as a sanity check — which is more than CI does, deliberately.
3. **Keep commit messages short and imperative.** "Fix the drain worker's progress check", not
   "Fixed a bug where...".
4. **The build check must pass** before a pull request can merge. It runs on every push to the
   branch — except on a first-time contributor's fork, where it waits for a maintainer to release
   it. A check that sits pending on your first pull request is that, not a failure.
5. **Every review thread must be resolved** before it can merge, and Copilot reviews every pull
   request, so expect at least one thread to answer even when no human has commented yet.

Integration suites (`tests/*.IntegrationTests`) need Docker and are excluded from the local gate;
they run in their own workflow after a merge.

### What the review looks for

The framework is application-agnostic by rule: it never references a consumer application, and
consumer-specific domain logic belongs in the consumer's own repository. If your change needs
Stratara to know something about your domain, the answer is usually an extension point rather than
the knowledge.

Public API surface carries XML documentation — `CS1591` is an error for packable projects, so the
build tells you before the review does. `openspec/specs/` states what the framework guarantees; a
change to behaviour that a consumer could observe belongs there too, and a pull request that alters
a guarantee without touching its specification will be asked about it.

## Releases

Versions are lockstep: one `<VersionPrefix>` in `Directory.Build.props` governs all 25 packages, and
a `v*` tag publishes them to nuget.org. What is not tagged is not published — there is no feed that
fills itself from `main`, so a change reaches a consumer when someone decides to release it.

A tag may name a prerelease: `v4.0.0-preview.1` publishes `4.0.0-preview.1`, behind the same
approval as any other release. It exists so a version can be tested before it is final, and it is
invisible to anyone who does not ask for prereleases.

## Code of Conduct

By participating in any Stratara space you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## License

Stratara is licensed under the [MIT License](LICENSE). Contributions are accepted under the same
terms; filing an issue transfers no IP to the project.
