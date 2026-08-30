## Context

See `proposal.md` — *Why*. What matters for the approach is the shape of the six defects, not their
motivation:

| Defect (audit, merged as PR 7674) | What would have caught it |
|---|---|
| `IBusEnvelopeSigner` snippet declaring `Sign(BusEnvelopeCanonical)` | a check that a re-declared type matches the real one — *not* a compiler; see decision 1b |
| `SharedKey` (a `byte[]`) assigned a configuration string | a compiler |
| Configuration section `BusIntegrity` instead of `BusEnvelopeIntegrity` | one assertion against `BusEnvelopeIntegrityOptions.SectionName` |
| Topic names `stratara.commands.{appName}` that the framework never used | one assertion against `MessagingIdentifier`'s defaults |
| Log-event allocation table three buckets behind `LogEvents` | one assertion enumerating the nested classes |
| `AddRedisOutboxLock` documented nowhere | one inventory assertion over public registration methods |

Two of six are a compilation problem and four are an *agreement* problem between a document and a
type. That split decides the tooling: a snippet compiler, and reflection-based assertions. Neither is
a grep — the existing `scripts/check-public-mirror.sh` greps because it looks for a forbidden
*string*, which is the one thing text search is right for.

Constraints that shape it:

- `TreatWarningsAsErrors=true` repo-wide, CS1591 is an error on packable projects, so every public
  member already carries *some* XML documentation. The work is raising its content, not filling
  holes.
- The gate a contributor actually runs is `./scripts/local-gauntlet.sh`; pipeline 24 runs the same
  tests. New checks must be tests or scripts that fit both, with no new infrastructure.
- The public mirror (pipeline 40) publishes an allowlist of top-level directories. Anything an
  external agent must fetch has to land inside it.
- `Stratara.Testing` is guarded by `STRATARA1001` against consumption from a non-test project, so
  verification code lives in a test project, not in a packable one.

## Goals / Non-Goals

**Goals:**

- A documented statement that disagrees with the code fails the build, at the commit that introduces
  the disagreement.
- An agent working in a consumer repository — with the packages but not this source tree — can
  answer "what is this option's configuration key, what does this registration require, what does
  this throw, what is this topic called" without reading prose.
- The verification is cheap to extend: adding a new asserted fact is a test method, not a tool.

**Non-Goals:**

- Re-authoring the documentation. The gate adapts to how the pages are written today; it does not
  impose a literate-programming style on them.
- Verifying prose. Nothing here checks whether an explanation is *good* — only whether a checkable
  claim is *true*.
- Generating narrative documentation. The generated artifact is a catalogue; the guides stay
  hand-written.
- Covering the sample projects with the snippet compiler. They already compile — they are projects.

## Decisions

### 1. Snippet compilation: extract and compile in a test, not "snippets are real files"

The stronger long-term pattern is the inverse of what this change does: snippets live in a compiled
project and are *included* into the markdown by a tool (the MarkdownSnippets model). It is rejected
here because it rewrites every page in `docs/` and changes how documentation is authored, which is a
re-authoring project wearing a verification project's clothes. The goal is a gate.

So: a test project extracts every ` ```csharp ` fence from `docs/**/*.md` and compiles it with
Roslyn against the framework assemblies it references.

Fences are fragments, so the harness classifies before it compiles:

- a fence containing a type declaration is wrapped in a namespace with a standard `using` set;
- a fence of statements is wrapped in a method body with the same set;
- a fence marked with an ignore directive is skipped, and the directive carries a reason.

The ignore directive is required for the fences that are legitimately not framework code — a
consumer's own `MapAccountEndpoints`, an `appsettings` shape shown as C#, a decision tree. Each one
is a deliberate, reviewable statement rather than a silent gap.

**Two additions, forced by the first run over `docs/`.** 51 of the 88 fences did not compile, and
none of the 51 was a documentation defect. They fail for two reasons, and each needs a mechanism:

- **A consumer-placeholder prelude**, checked in and compiled alongside every snippet. 66 errors were
  unknown types that are *correct* documentation — `AddCommandHandlersFromAssemblyContaining<Program>()`
  names the reader's `Program`, and the guides share the samples' bank-account domain. The prelude is
  the consumer code the documentation assumes: `Program`, the marker types, `AppDbContext`,
  `ApplicationUser`, and the example domain. It is deliberately a shadow domain — its cost is that it
  must keep pace with the names the documentation invents, and its benefit is that every *framework*
  call in those fences is checked instead of exempted.
- **Cumulative page context.** 82 errors were names introduced by an earlier fence on the same page:
  the guides build a scenario across several blocks. A fence is therefore compiled together with the
  preceding non-ignored fences of its own page, which is how the page reads.

Without both, the honest alternative is an ignore directive on 51 of 88 fences — which would leave
the check covering the short self-contained snippets and exempting exactly the long,
framework-heavy sequences where drift lives. That is the silent narrowing this change exists to
prevent, so the mechanisms are the cheaper price.

### 1b. A fence that re-declares a framework type is checked against it

Compilation does not catch a drifted type declaration. A fence declaring
`interface IBusEnvelopeSigner { string Sign(BusEnvelopeCanonical canonical); }` compiles: it declares
a *new* type in the synthetic namespace, and the real one is simply a different type with the same
name. *Evidence: written as a regression test during implementation of task 2.2, and it failed —
the defect that started this change would have passed the snippet compiler.*

So: when a fence declares a type whose simple name also exists in the framework assemblies, the
declaration is compared against the real type — member names, arity, parameter and return types. A
mismatch fails with both shapes printed. Four of the five type re-declarations across `docs/` already
matched; the fifth was the defect.

Alternative rejected: forbidding type re-declaration in documentation outright. Showing an interface
is how you explain what a consumer must implement, and the four correct ones are good pages.

### 2. Agreement assertions use reflection, not text search

Every assertion resolves the fact from the *type*, never from the source text: the section name from
`BusEnvelopeIntegrityOptions.SectionName`, the topic defaults from `MessagingIdentifier`, the buckets
by enumerating `LogEvents`'s nested classes and their constants, option defaults by instantiating the
options class. The test then asserts the document contains it.

Grepping `src/` for the same fact would give a check that drifts in the same way the documentation
drifts — it would still be reading text. Reflection reads what ships. *Evidence: the `BatchSize`
defect (3.4.0) was a doc that contradicted `OutboxWorker`'s own XML comment — two texts disagreeing,
with no third party that knew.*

### 3. The inventory rule keys on type, not on name prefix

"Every registration appears in the cheatsheet" cannot key on the `Add`/`Map`/`Use` prefix: the ad-hoc
sweep that found `AddRedisOutboxLock` also flagged `AddRangeAsync`, `MapTo` and `AddPaymentCard`. The
rule is therefore: a public static extension method whose first parameter is `IServiceCollection`,
`IHostApplicationBuilder`, `IHealthChecksBuilder`, `AuthenticationBuilder` or `IApplicationBuilder`
must appear in `docs/reference/di-extensions-cheatsheet.md`.

Deliberate omissions live in a small allowlist file with a reason per entry, not in the test.

### 4. The catalogue is generated into `llms-full.txt` at the repository root

`llms-full.txt` is the companion the `llms.txt` convention already defines, so an agent that finds
`llms.txt` finds it without being told. The root is inside the mirror allowlist; `llms.txt` is
already there.

It is **generated and committed**, not generated at build time: an agent fetches a file from the
mirror, so the file must exist in the tree. A test asserts that regenerating produces no diff — the
same trick the assertions use, applied to a whole artifact.

The generator is a non-packable console project. It reads the built assemblies by reflection and the
XML documentation files the build already emits next to them, and writes deterministically ordered
Markdown: options classes with section name, key paths and defaults; registration methods with their
prerequisites; the framework's exception types; and the topic, table and cache-key names. Markdown
over JSON because both agents and people read it, and because `llms.txt` sets that precedent.

Alternative rejected: a DocFX plugin. DocFX already generates the API reference from the same XML,
and that reference is *not* what is missing — a member-by-member wall does not answer "what does this
registration require". The catalogue is a different cut of the same data.

### 5. The XML doc obligation is scoped by the same rule as the inventory

Every method in the set from decision 3 states, in its XML documentation: the configuration key path
it binds (when it binds one), its prerequisites and ordering constraints, and one `<example>`. The
inventory test grows a second assertion for the presence of the `<example>` element, so the
obligation holds for methods added later.

This is the decision with the largest manual share — roughly a hundred methods — and the one with the
most direct effect, because the `.nupkg` XML is the only channel that reaches a consumer's agent at
the call site. *Evidence: the integrity defect that started this sat in exactly that channel, in
`BusEnvelopeIntegrityOptions`.*

### 6. Fix first, then enforce

Each check is landed in two steps: the failures it finds are fixed, and only then does the check join
the gauntlet. `main` never goes red for a pre-existing defect, and the fix commits stay readable as
"here is what was wrong" rather than being buried under the tooling that found it.

## Risks / Trade-offs

- **Casual documentation edits get harder — a fence must now compile.** → That is the intended cost,
  but it is a real one. Mitigated by the ignore directive for genuinely non-compiling illustrations,
  and by the harness supplying the usings so a snippet stays as short as it reads today.

- **The snippet compiler needs the framework assemblies, so the test project references many
  projects and the unit-test pipeline gets slower.** → One project, references only, no new
  dependency. If the cost shows, the snippet tests can be split into their own gauntlet step without
  changing the approach.

- **A generated file that is committed can be stale in a branch.** → The no-diff test fails the build
  in exactly that case, which is the same mechanism as every other assertion here.

- **The catalogue could grow into noise nobody reads.** → It is scoped to the four categories in
  decision 4, which are the ones an agent cannot recover from the package surface. It is not an API
  dump; DocFX already produces one of those.

- **XML documentation can still be *wrong* while being present.** → No check proposed here catches a
  false sentence in prose; the `<example>` blocks are the part the snippet compiler can reach, and
  bringing them under it is what makes the obligation more than a formality.

- **The two labelled samples still teach a re-implementation, now with a warning on top.** → The
  label is the cheap fix, not the complete one. Whether those samples should reference the real
  packages is a separate decision this change deliberately leaves open.

## Migration Plan

Additive throughout; nothing is removed and no consumer action is required. The order is decision 6
applied three times — fix, then enforce — taking the checks in the order of their yield: snippet
compilation, then the agreement assertions, then the inventory rule. The XML documentation pass and
the catalogue generator follow, in that order, because the catalogue reads what the pass writes.

Rollback is per check: each is a test and a gauntlet line, removable on its own without touching the
documentation it verified.
