## 1. Verification project scaffold

- [x] 1.1 Create `tests/Stratara.Documentation.Tests` (xUnit v3, `IsPackable=false`), referencing
      every project in `Stratara.Publish.slnf` so reflection and snippet compilation see the whole
      public surface. Done when `dotnet test tests/Stratara.Documentation.Tests` runs and reports
      zero tests.
- [x] 1.2 Add `RepositoryRoot.Locate()` — walks up from `AppContext.BaseDirectory` to the directory
      containing `Directory.Build.props` — plus a `DocumentationFiles` helper that enumerates
      `docs/**/*.md` excluding `docs/_site/` and `docs/reference/api/`. Proven by
      `RepositoryRootTests` asserting the located root contains `Stratara.Publish.slnf`, and by a
      test asserting the enumeration is non-empty and contains no `_site` path.

## 2. Snippet compilation (design decision 1)

- [x] 2.1 Implement `SnippetExtractor` — pulls every ` ```csharp ` / ` ```cs ` fence out of a
      markdown file with its file path and line number, and honours an ignore directive
      (`<!-- stratara-snippet-ignore: <reason> -->` on the line before the fence). Unit-tested in
      `SnippetExtractorTests` against fixture markdown covering: type-level fence, statement-level
      fence, ignored fence, fence with no reason (must throw).
- [x] 2.2 Implement `SnippetCompiler` — classifies a snippet as type-level or statement-level, wraps
      it with the standard `using` set, and compiles it with Roslyn against the referenced
      assemblies. `SnippetCompilerTests` proves a known-good snippet compiles and a known-bad one
      reports the originating file and line.
- [x] 2.3 Add the consumer-placeholder prelude (`Fixtures/ConsumerPlaceholders.cs`) and cumulative
      page context to the harness (design decision 1): the prelude is compiled alongside every
      snippet, and a fence is compiled together with the preceding non-ignored fences of its own
      page. `SnippetCompilerTests` gains a case proving a fence that uses a name from an earlier
      fence on the same page compiles, and one proving `Program` resolves.
- [x] 2.4 Implement the type-shape check (design decision 1b) as `DocumentationTypeShapeTests` —
      when a fence declares a type whose simple name exists in the framework assemblies, compare
      member names, arity, parameter and return types, and fail with both shapes printed. Done when
      `SnippetCompilerTests.Compile_ReportsTheSignerSnippetThatShippedBefore341` — currently failing
      on purpose — turns green through this check.
- [x] 2.5 Run the harness over `docs/` and fix every failure it reports, or mark the fence with an
      ignore directive and its reason. Done when the run is clean; the diff is the record of what
      was wrong.
- [x] 2.6 Turn it into `DocumentationSnippetsCompileTests` — one test case per fence, failing with
      the file, line and compiler diagnostic.

## 3. Agreement assertions (design decision 2)

- [x] 3.1 `ConfigurationSectionNameTests` — for every options type exposing a `SectionName` constant,
      assert the constant's value appears in the page that documents it and that no *other* section
      name is used for it. Catches the `BusIntegrity` / `BusEnvelopeIntegrity` defect.
- [x] 3.2 `MessagingTopicNameTests` — resolve the defaults from `MessagingIdentifier` (`command`,
      `heavy-command`, `event-bundle`, `notifications` and their subscriptions) and assert
      `docs/guides/outbox-setup-rabbitmq.md` documents each one.
- [x] 3.3 `LogEventAllocationTests` — enumerate `LogEvents`'s nested classes and their constants,
      derive the occupied buckets, and assert `docs/reference/log-events-schema.md` has a row for
      each and that its stated upper bound matches the highest allocated bucket.
- [x] 3.4 `OptionDefaultTests` — instantiate every options type in the publish set and assert that
      each default value a document states for one of its properties equals the instantiated value.
- [x] 3.5 Fix whatever 3.1–3.4 report on the current tree before wiring them into the gate.

## 4. Registration inventory (design decision 3)

- [x] 4.1 Implement `RegistrationSurface.Enumerate()` — public static extension methods whose first
      parameter is `IServiceCollection`, `IHostApplicationBuilder`, `IHealthChecksBuilder`,
      `AuthenticationBuilder` or `IApplicationBuilder`. `RegistrationSurfaceTests` asserts it finds
      `AddRedisOutboxLock` and `AddBusEnvelopeIntegrity` and does *not* find `AddRangeAsync` or
      `MapTo`.
- [x] 4.2 `DiCheatsheetCoverageTests` — every method from 4.1 appears in
      `docs/reference/di-extensions-cheatsheet.md`, except those listed in
      `tests/Stratara.Documentation.Tests/registration-coverage-allowlist.txt` with a reason per
      line. Test fails on an allowlist entry that no longer resolves to a method.
- [x] 4.3 Fill the gaps 4.2 reports in the cheatsheet, or allowlist them with a reason.

## 5. XML documentation on the registration surface (design decision 5)

- [x] 5.1 Write the house rule down where the project's documentation policy lives: a registration
      method documents its configuration key path (when it binds one), its prerequisites and
      ordering constraints, and carries one `<example>`.
- [x] 5.2 Work through the methods from 4.1 package by package, starting with
      `Stratara.Infrastructure`, `Stratara.Outbox.RabbitMQ` and
      `Stratara.EventSourcing.EntityFrameworkCore` — the three that carry the registrations a
      consumer gets wrong. Each package is done when its methods satisfy 5.1.
- [x] 5.3 `RegistrationDocumentationTests` — every method from 4.1 has a non-empty `<example>` in the
      generated XML documentation file next to its assembly. Wire it in only after 5.2 is complete
      for every package.
- [x] 5.4 Confirm the `<example>` bodies are covered by the snippet compiler from section 2 or, if
      they are not reachable from markdown, extend `DocumentationSnippetsCompileTests` to read the
      generated XML documentation files as a second source.

## 6. Generated reference catalogue (design decision 4)

- [x] 6.1 Create `tools/Stratara.ReferenceCatalogue` (console, `IsPackable=false`, excluded from
      `Stratara.Publish.slnf`) that reflects over the built publish-set assemblies plus their
      generated XML documentation files.
- [x] 6.2 Emit deterministically ordered Markdown to `llms-full.txt` at the repository root, in four
      sections: options classes with section name, key paths and defaults; registration methods with
      prerequisites; framework exception types; and the topic, table and cache-key names.
- [x] 6.3 `ReferenceCatalogueIsCurrentTests` — regenerate into a temporary file and assert byte
      equality with the committed `llms-full.txt`.
- [x] 6.4 Link `llms-full.txt` from `llms.txt` and remove from `llms.txt` the hand-written prose the
      catalogue now covers.
- [x] 6.5 Confirm `llms-full.txt` reaches the public mirror — it sits at the repository root, which
      `scripts/sync-to-github.sh` already carries; verify with a dry run rather than assuming.

## 7. Sample honesty fixes

- [x] 7.1 Add a prominent note at the top of `samples/Stratara.Sample.MoneyTransferSaga/README.md`
      and `docs/samples/04-money-transfer-saga.md`: the saga here is hand-written for teaching and
      the framework provides `ISaga` in `Stratara.Sagas` — with a link to
      `docs/guides/write-a-saga.md`.
- [x] 7.2 The same for `samples/Stratara.Sample.OutboxWorker/README.md` and
      `docs/samples/03-outbox-worker.md`, pointing at `Stratara.Outbox.RabbitMQ` and
      `docs/guides/outbox-setup-rabbitmq.md`.
- [x] 7.3 Narrow the CI claim in `samples/README.md`: the smoke tests catch an API break in the
      packages the samples reference (`Stratara.Mediator`, `Stratara.Validation`,
      `Stratara.Identity.AspNetCore`, `Stratara.Identity.EntityFrameworkCore`), not across all 25.

## 8. Gate and record

- [x] 8.1 Add `tests/Stratara.Documentation.Tests` and the catalogue no-diff check to
      `./scripts/local-gauntlet.sh`, so both fail before a push rather than in pipeline 24.
- [x] 8.2 Verify the whole gate is green from a clean tree: `./scripts/local-gauntlet.sh`.
- [x] 8.3 CHANGELOG entry under `[Unreleased]` — the richer XML documentation inside every `.nupkg`
      and the new `llms-full.txt` are the two consumer-visible effects; say explicitly that no API
      or behaviour changes.
