# Reviewing a pull request in this repository

Stratara is an application-agnostic CQRS, Event-Sourcing and Mediator framework for .NET 10,
shipped as 25 NuGet packages at one lockstep version. It is a library, not an application: what a
consumer relies on is the published API of those packages, and almost every judgement about a
change follows from that.

The contribution rules are in [`CONTRIBUTING.md`](../CONTRIBUTING.md) and this file does not repeat
them. What follows is what a reviewer needs on top: the house rules that look like defects when you
do not know them, and where the contract that decides an argument lives.

## What decides whether the change is right

`openspec/specs/` is the contract — one file per capability, stating what the framework guarantees
to a consumer of a published package. Where a guide, a comment or a README disagrees with it, the
specification is right and the other is the defect. A change to observable behaviour arrives as an
approved change under `openspec/changes/` carrying a spec delta; a pull request that alters a
guarantee without one is worth raising.

The build conventions are stated in full in `openspec/config.yaml` under
`operations.apply.guidance`. That file is the rule; nothing else in the repository restates it.

## Do not suggest these

Each of these is a deliberate rule, so a suggestion against it costs the author a reply and teaches
them to skim your review.

- **Do not ask for explanatory comments, and do not add them.** A spot that needs a comment is code
  that is not readable enough, and the fix is to restructure it. XML documentation on the public
  members of a packable project is the exception, and the build already enforces it — `CS1591` is
  an error there. Rationale belongs in the design note of the change that made the decision.
- **Do not suggest `logger.LogInformation()` and friends.** Logging is source-generated only:
  `[LoggerMessage]` on a partial method in a partial class, the exception parameter first after
  `this ILogger logger`, PascalCase placeholders, event IDs from `Stratara.Diagnostics/LogEvents.cs`.
- **Do not suggest `ConfigureAwait(false)`.** It is deliberately not used in this repository.
- **Do not suggest a `Stopwatch`.** Allocating one is forbidden; `Stopwatch.GetTimestamp()` is fine
  and is what the framework's own workers use.
- **Do not suggest `private set` on an aggregate's properties.** Aggregates are sealed classes with
  public setters because snapshot JSON deserialization needs them.
- **Do not suggest an underscore prefix on a private field.** camelCase, no prefix, throughout.
- **Do not suggest a per-project `<Version>`.** One `<VersionPrefix>` in `Directory.Build.props`
  governs all 25 packable packages, and lockstep is the point.
- **Do not ask for a factoring-out on the second repetition.** DRY applies from the third.
- **Do not cite a decision record or an ADR number.** That corpus was dissolved into
  `openspec/changes/archive/`; a number resolves to nothing. Say the thing itself.
- **Do not propose abstracting for a case that does not exist yet.** A premature interface on a
  published surface is a finding rather than an improvement — every one of them ships to consumers.
- **Do not suggest naming an internal type, namespace, project folder or table in a specification.**
  A requirement describes what a consumer observes. Published API names are the consumer's
  vocabulary and are allowed; implementation names are not.

## Worth raising

- Code that disagrees with the capability it belongs to in `openspec/specs/`, or observable
  behaviour changing with no spec delta in the change that carries it.
- **A tier violation.** Tier-A (`Abstractions`, `Contracts`, `Diagnostics`, `Resilience`) →
  Tier-B (`Mediator`, `Domain`, `Shared`, `Sessions`, `ServiceDefaults`) → Tier-C (everything
  else). Tier-N may reference only Tier-≤N, and nothing enforces it mechanically yet.
- **Consumer-specific or domain knowledge reaching the framework.** Application-agnosticism is the
  rule the whole design exists for; the answer to "Stratara needs to know about my domain" is an
  extension point, not the knowledge.
- **A renamed metric, meter, activity or tag name.** Those are a public observability contract and
  renaming one is a breaking change, however internal the code around it looks.
- **A persisted enum value removed rather than marked `[Obsolete]`.** Existing event streams still
  carry it.
- A published surface changing shape without the version consequence being acknowledged — a
  removed or renamed public member is a major, a new type or overload is at least a patch.
- A `DbContext` sharing an assembly with sibling contexts whose `ApplyConfigurationsFromAssembly`
  call has no namespace predicate. Without it EF picks up the siblings' configurations and the
  consumer gets a `PendingModelChangesWarning` that local unit tests do not catch.
- A new csproj referenced by a packable one that is not itself packable and listed in
  `Stratara.Publish.slnf` — the parent nuspec would declare a dependency on a package that is not
  on the feed.
- `Thread`, `new Task(...)`, `Task.Run`, a manual retry loop with `Task.Delay`, `dynamic`,
  reflection where pattern matching works, or `null!` outside generated code and interop. Consumers
  may use them; Stratara may not.
- A consumer-visible change with nothing under `## [Unreleased]` in `CHANGELOG.md`. Test-only,
  doc-only and CI-only changes do not need an entry.
- A file in this repository pointing at a path outside it — in particular anything under
  `.claude/`. That directory is linked in from a private repository, so the path resolves for the
  person who wrote it and for nobody else. The fix is to write what the referenced file says.
- A bug fix without a test that fails against the old code. A test that passes either way proves
  nothing.
- Text that is not English anywhere in the repository, including commit messages and pull request
  descriptions. The repository is public.
- AI attribution in a commit message or a pull request description. It does not belong there.

## How to pitch a review

Prefer few findings that are certainly right over many that might be. A review of a version bump, a
dependency update or an archived change should usually say nothing. When a finding is a matter of
taste rather than a rule, say so in the comment, so the author can close it without a debate.
