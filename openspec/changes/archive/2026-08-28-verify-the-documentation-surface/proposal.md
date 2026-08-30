> **Status:** approved

# Verify the documentation surface, and aim it at the reader it actually has

## Why

**Nothing a consumer can call changes.** No API, no behaviour, no packaging. What changes is how
much of the documentation is *checked* rather than believed, and what shape it has for the reader
who now does most of the reading.

Two findings, one week apart, and they point the same way.

A consumer team turned on bus-envelope integrity and nearly did not, because three documents —
two XML doc comments and `SECURITY.md` — described the pre-3.4.0 signature scope and advised
building a redundant integrity layer. What saved it was that somebody read the source instead. The
follow-up audit over the whole documentation surface found five more of the same kind, and this is
the part that decides what to do about it: **every one of the six was mechanically checkable.**

- A `csharp` snippet declaring an interface that does not compile against the real one.
- A configuration example binding a section name that does not exist.
- Topic names in two guides that the framework has never used — an operator provisioning a broker
  from those pages creates topics nothing publishes to.
- An encryption guarantee documented as conditional on an unrelated option.
- A log-event ID allocation table three buckets behind the code, in the one table a consumer reads
  to pick a non-colliding range.
- The registration that makes a second outbox-worker replica safe, absent from every page.

None of these needed judgement to catch. A compiler and a handful of assertions would have caught
all six at the commit that introduced them. They survived because prose is the one artifact in this
repository that CI does not read.

The second finding is about the reader. The consuming code is increasingly written by agents, and an
agent reads differently from a person: it greps rather than learns, it extrapolates a whole API from
one correct example, and it fills gaps by inventing something plausible. That inverts what costs the
most. A missing page costs little — but a *plausible wrong sentence* is followed with full
confidence, and an *absence* is filled with an invention. Both of this week's expensive defects were
exactly those two shapes. It also relocates the channel that matters: an agent working in a consumer
repository cannot read this source tree. It has the packages — and the XML documentation that ships
inside them is the only always-available, always-version-matched channel Stratara has to it. That is
precisely where the integrity defect sat.

**The alternative considered and rejected: more samples.** The samples exercise 5 of the 25 packages
and re-implement in hand-written form what the other 20 provide. A sample is the wrong instrument
here twice over — it does not raise the floor for an agent that extrapolates from one example, and
two of the existing ones actively teach the re-implementation instead of the framework. Adding to
that set would grow the surface that can drift while fixing none of the six defects above.

## What Changes

- **Documentation snippets compile in CI.** Every `csharp` fence in `docs/` is extracted and
  compiled against the framework assemblies; a snippet that does not compile fails the build.

- **A fence that re-declares a framework type must match it.** Compilation alone does not catch a
  documented interface whose shape has drifted — a fence declaring `IBusEnvelopeSigner` declares a
  new, unrelated type, and the compiler has no reason to object. Proven by a failing regression test
  during implementation: of the six defects, compilation catches one and this check catches the one
  that started the change.

- **The facts get assertions.** Tests that compare documentation against source for the things that
  drifted: configuration section names, messaging topic and subscription defaults, log-event ID
  bucket allocation, documented option defaults, and the inventory rule that every public
  `Add*` / `Map*` / `Use*` extension appears in the DI cheatsheet. Absence becomes a build failure
  rather than something a reader has to notice.

- **The registration surface documents its contract in XML.** Every public registration method
  states its configuration key path, its prerequisites and ordering constraints ("needs an
  `IConnectionMultiplexer`", "call before `AddSecurity()`"), and carries one `<example>`. This is
  what reaches a consumer's agent at the call site.

- **A generated reference catalogue.** Derived from source, not written: every options class with
  its section name, key paths and defaults; every registration with its prerequisites; every
  exception the framework throws; every topic, table and cache-key name. Generated means it cannot
  drift. It replaces the hand-written prose in `llms.txt` for those parts and ships in the public
  mirror, where an agent can fetch it.

- **Two honesty fixes in the samples.** `Stratara.Sample.MoneyTransferSaga` and
  `Stratara.Sample.OutboxWorker` are labelled at the top as hand-written illustrations that
  re-implement what `Stratara.Sagas` and `Stratara.Outbox.RabbitMQ` provide, with a pointer to the
  guide — an agent that reads them today copies the re-implementation. And `samples/README.md`
  promises that a breaking API change fails CI; that holds for the 5 packages the samples reference,
  not for 25. The claim is narrowed to what is true.

- **No new samples**, and the reasoning above is recorded here so the question is not reopened
  without new evidence.

## Capabilities

### New Capabilities

None. No capability gains, loses or alters a requirement.

### Modified Capabilities

None. `skip_specs: true` — this change adds verification and documentation, and specifications
describe behaviour. The guarantees in `openspec/specs/` are exactly what they were; the point of the
work is that the documentation stops contradicting them.

## Impact

Consumer-visible: richer XML documentation inside every `.nupkg`, and one new generated reference
file in the public mirror. Nothing else — no type, member, signature, default, configuration key or
runtime behaviour changes.

Affected in this repository:

- **New**: `tests/Stratara.Documentation.Tests` (the verification project),
  `tools/Stratara.ReferenceCatalogue` (the generator) and `llms-full.txt` (its output). Both projects
  are added to `Stratara.slnx`; neither is packable and neither is in `Stratara.Publish.slnf`.
- `scripts/local-gauntlet.sh` — the new checks join the local gate, so they fail before a push
  rather than in pipeline 24, and the gate now fails rather than skipping when the verification
  project has not been built.
- `scripts/sync-to-github.sh` — `llms-full.txt` and `tools/` join the mirror's allowlists; without
  the entry the catalogue would never reach the public tree.
- The project's documentation policy — the house rule for what a registration method's XML
  documentation must carry.
- `docs/` — snippets and tables become the checked artifact; corrections wherever an assertion
  disagrees with the source.
- `llms.txt` — the hand-written option, registration, exception and name sections give way to the
  generated catalogue.
- `src/**/DependencyInjection/*.cs` and the other public registration entry points — XML doc
  comments only.
- `samples/README.md`, `samples/Stratara.Sample.MoneyTransferSaga/README.md`,
  `samples/Stratara.Sample.OutboxWorker/README.md`, and the matching pages under `docs/samples/`.

`CHANGELOG.md` carries the consumer-visible half under `[Unreleased]`.

Nothing here is dissolved or superseded: no existing document is retired by this change.
