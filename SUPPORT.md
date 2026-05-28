# Getting help with Stratara

Stratara is developed primarily as an in-house framework, with limited public support
at this stage. Here is how to find help.

## Documentation first

The full documentation site is the canonical reference:

**<https://docs.stratara.tech>**

It covers:

- **Overview** — what Stratara is and isn't.
- **Getting Started** — install + first mediator + first event-sourced aggregate.
- **Guides** — task-oriented walkthroughs for the common stacks.
- **Samples** — five self-contained sample apps, each runnable with `dotnet run`.
- **API Reference** — auto-generated from the XML docs of every public type.

## Asking questions

For questions about how to use Stratara, please open an issue using the **Question**
template:

<https://github.com/yesbert/Stratara/issues/new/choose>

Before filing, please:

1. Check the documentation linked above.
2. Check existing open and closed issues — your question may already be answered.

We don't currently use GitHub Discussions; questions go through the Issue tracker
with the `question` label.

## Reporting bugs

For bugs, please open an issue using the **Bug Report** template. The more
reproducible, the better — include Stratara version, .NET version, and a minimal
repro if possible. Details in [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Security issues

Please **do not file a public issue** for security vulnerabilities. Follow the
process described in [`SECURITY.md`](SECURITY.md).

## Commercial support

We do not offer paid support contracts for Stratara at this time. If you have a
commercial use case that needs a dedicated support agreement, you can reach out to
`github@stratara.tech`, but please understand that we may not be able to accommodate
all requests.

## What to expect

- **Bugs:** triaged on our internal tracker; fixes land in the next `v*` release.
  Typical turnaround is days to weeks. No SLA.
- **Questions:** best-effort answers, no SLA.
- **Feature requests:** not currently accepted — see [`CONTRIBUTING.md`](CONTRIBUTING.md).
