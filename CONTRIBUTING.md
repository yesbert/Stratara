# Contributing to Stratara

Thanks for your interest in Stratara.

## Current contribution model

Stratara is developed primarily as an in-house framework, with its **source-of-truth
hosted on an internal Azure DevOps repository**. The `github.com/yesbert/Stratara` repo
is a public mirror — every release is force-pushed as a single squashed commit.

Because of that mirror architecture, **we currently do not accept pull requests**.
Any external PR opened against the GitHub mirror would be lost on the next release sync.

This may change once the internal-vs-public boundary stabilises. For now we want to be
transparent about it so nobody invests effort in a PR that we can't merge.

## What we welcome

| Type | How |
|---|---|
| **Bug reports** | Open an issue with the `bug` template. The more reproducible, the better — include Stratara version, .NET version, and a minimal repro if possible. |
| **Questions** | Open an issue with the `question` template. Please check the docs first: <https://docs.stratara.tech>. |
| **Security issues** | Please follow [`SECURITY.md`](SECURITY.md) — do **not** file a public issue. |
| **Documentation feedback** | Issues are fine. The docs live in `docs/` and follow the same release cadence as the code. |

## What happens to your bug report

1. We triage it on the internal Azure DevOps tracker.
2. If it's a valid bug, we either fix it in the next internal release cycle or, for
   urgent issues, in a hotfix.
3. The fix lands in the next public release (next `v*`-tag push to the mirror).
4. We close the GitHub issue with a link to the release that contains the fix.

The typical turnaround for non-urgent bugs is days to weeks, depending on internal
priorities. We don't have an SLA for OSS support.

## What about feature requests?

Right now, please **do not file feature requests**. We're focused on stabilising the
existing surface and migrating consumers internally. Issues with feature-request content
will be closed politely with a pointer to this section.

When we open up contributions, we'll switch this section.

## Code of Conduct

By participating in any Stratara space (issues, future discussions, future PRs), you
agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## License

Stratara is licensed under [FSL-1.1-MIT](LICENSE). Filing an issue does not transfer
any IP to the project — but any code you might contribute in the future (once PRs
are accepted) would be licensed under the same terms.
