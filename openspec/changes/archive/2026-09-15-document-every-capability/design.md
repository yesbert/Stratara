# Design — Document every capability

## Context

See `proposal.md` → *Why*. The documentation site is derived from the specs: a derived page opens
with a header naming its capability, carries front matter with a `title` and a `description`, and is
listed in its section's `toc.yml`. `tests/Stratara.Documentation.Tests` already enforces option
defaults, section names, messaging names, log-event bands, cheatsheet coverage, front matter,
compiling fenced C#, and interface shapes. What it does not enforce is prose.

Two facts shape the test decisions, read 2026-09-14:

- `DocumentationCorpus.MentionsToken` matches a token between two characters that are not letters,
  digits or underscores. A dot is neither, so `SessionContext.TenantId` counts as naming the
  configuration section `SessionContext`, and `ConfigurationSectionNameTests` passes although no page
  names the section.
- `FrameworkSurface` loads every `Stratara.*.dll` beside the test assembly and exposes their exported
  types; the test project's output also holds the Microsoft and third-party assemblies those packages
  depend on.

## Goals / Non-Goals

**Goals:**

- Every requirement of the fourteen capabilities in `proposal.md` has a place on the site where a
  consumer finds it, written from the requirement.
- The two test weaknesses closed, each proven by a test that fails on the text that slipped through.

**Non-Goals:**

- Rewriting pages that already cover their requirements.
- Checking member names, method signatures or parameters in prose — only type names.
- A page per requirement, or a page per capability where an existing page is the natural home.

## Decisions

### D1 — A new page only where a capability has no natural home

`session-context`, `observability` and the queued-work half of `host-composition` get pages of their
own, because no existing page is about them and each has more than one requirement to carry. Every
other gap goes into the page a consumer already reads for that task: the handler guide for who owns
an event, the snapshot guide for rehydration, the projection and saga guides for their handlers and
replay, the outbox guide for the lock, the encryption guide for key storage, the packages page for
debugging into the source, and so on.

*Rejected: one page per capability for all fourteen.* Nine of them would be a page of two paragraphs
duplicating context the task guide already gives.

Evidence: `docs/concepts/toc.yml`, `docs/guides/toc.yml`; the requirement headings of the fourteen
specs.

### D2 — Session context is a concept, observability and queued work are guides

The session context is a model a reader has to hold before any guide makes sense, so it sits in
`docs/concepts/` beside tenant-aware encryption, which depends on it. Observability and queued work
are things a reader sets up and uses, so they sit in `docs/guides/`.

Evidence: `docs/concepts/tenant-aware-encryption.md` (built on `SessionContext.TenantId`).

### D3 — A section name followed by a member access is not the section

The token boundary also refuses a dot followed by a letter, digit or underscore. `"SessionContext": {`
in a configuration block and `SessionContext` at the end of a sentence still count;
`SessionContext.TenantId` does not.

*Rejected: requiring the section name inside a configuration block.* Several pages name a section in
prose before the block, correctly; the test would demand a block where the page does not need one.

Evidence: `DocumentationCorpus.cs:19-20`; `ConfigurationSectionNameTests.cs` (gains a case for the
member access).

### D4 — Framework-shaped type names in inline code must exist

A new test extracts every inline code span from `docs/**/*.md` and takes the leading identifier —
generic arguments, a member access and a call stripped. It checks the identifier only when it has the
shape of a framework type: it ends in one of the suffixes the framework uses for types a consumer
names (`Worker`, `Exception`, `Options`, `Provider`, `Service`, `Dispatcher`, `Repository`, `Store`,
`Behavior`, `Middleware`, `Attribute`, `Host`, `Tester`) or starts with `Stratara`. Such an identifier
must be an exported type of a published assembly, a type of an assembly beside the test, or declared
in a fenced block of the same page. A short allowlist in the test carries the rest, each entry with
the reason.

*Rejected: checking every PascalCase identifier.* Pages are full of consumer example types —
`AccountOpened`, `TransferSaga` — that exist nowhere but in the example; the allowlist would be the
test.

*Rejected: checking members too.* A member name is ambiguous without its receiver, and the interface
shape test already pins the members that matter.

Evidence: the three names the review found (`SagaOrchestrationWorker`, `EventProjectionWorker`,
`OptimisticConcurrencyException`) and `TestSessionContextProvider`, which exists — the test gains a
case asserting the first three fail and the fourth passes.

### D5 — Every addition is written from its requirement and says which

Each addition opens its section with the behaviour as the requirement states it, in the page's own
voice, and the page's derivation header names every capability the page now draws on. A reviewer
checks an addition against the requirement heading the proposal names for it.

Evidence: the derivation header convention on every page under `docs/`.

## Risks / Trade-offs

- **[The suffix list misses a future wrong name]** → It covers every kind of type the review found
  wrong and the kinds a consumer is told to register or catch; a later miss adds a suffix, not a
  second mechanism.
- **[The token boundary change reports other sections as undocumented]** → That is the point; each
  report is a section no page names, fixed in this change's first task before any page is added.
- **[Pages drift from the specs again]** → The derivation header and the section-name, option-default
  and type-name tests are what catch drift mechanically; prose that states a behaviour wrongly still
  needs a reviewer, as before.

## Migration Plan

None. The site deploys on merge through its workflow, behind its approval.
