## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. Documentation tests first (D3, D4)

- [x] 1.1 The token boundary in `DocumentationCorpus.MentionsToken` refuses a dot followed by an
      identifier character. Verify: a new case in `ConfigurationSectionNameTests` asserts
      `SessionContext.TenantId` does not name the section and `"SessionContext": {` does; the test run
      lists every section that is now reported as undocumented, and each is added to the task that
      documents it below.
- [x] 1.2 A new test class checks framework-shaped type names in inline code against the published
      assemblies, the assemblies beside the test, and the page's own fenced blocks, with a commented
      allowlist. Verify: a case asserts that `SagaOrchestrationWorker`, `EventProjectionWorker` and
      `OptimisticConcurrencyException` fail and `TestSessionContextProvider` passes; the class is green
      on `main`'s pages after the allowlist review.

## 2. New pages (D1, D2, D5)

- [ ] 2.1 `docs/concepts/session-context.md` from `session-context`, all eight requirements, including the
      `SessionContext` section and `AllowTenantHeader`; listed in `docs/concepts/toc.yml`. Verify:
      `ConfigurationSectionNameTests` green for the section; front matter and derivation header present
      (`SiteMetadataTests`); each requirement heading of the spec maps to a paragraph.
- [ ] 2.2 `docs/guides/observe-the-framework.md` from `observability`: one source and one meter, the
      instrument contract and its constants, every instrument with its tags, header redaction, protected
      fields out of log messages; links to `docs/reference/log-events-schema.md`; listed in
      `docs/guides/toc.yml`. Verify: every instrument name on the page equals a constant in
      `ApplicationDiagnostics` (checked by the inline type-name test and a read against
      `src/Stratara.Diagnostics`), and the page covers *Instrument names are a stable published
      contract*, *All framework telemetry originates from one source and one meter*, *Sensitive request
      headers are redacted from traces* and *Protected field values never appear in log messages*.
- [ ] 2.3 `docs/guides/queue-background-work.md` from `host-composition`: *Work can be queued for
      in-process execution*, *Queued work reports its own outcome*, *Status retention is bounded*,
      *Background execution is parallel and ordered on entry*; listed in `docs/guides/toc.yml`; the
      cheatsheet row links to it. Verify: `DocumentationSnippetsCompileTests` green for its snippets and
      `DiCheatsheetCoverageTests` green.

## 3. Additions to existing pages (D1, D5)

- [ ] 3.1 `docs/guides/write-a-command-handler.md`: *The owning tenant is resolved from the stream before
      the session* and *A concurrency conflict discards the batch and is distinguishable*. Verify: the
      snippets compile; `AppendOnBehalfOfAsync` and `ConcurrencyException` pass the type-name test.
- [ ] 3.2 `docs/getting-started/di-composition.md` and the cheatsheet: *The store declares its own schema*
      — the constraints a migration carries and the namespace filter for contexts that share an
      assembly. Verify: a read against `event-sourcing-store` → *The store declares its own schema* and
      its three scenarios.
- [ ] 3.3 `docs/guides/configure-snapshots.md`: *An unhandled event is skipped rather than rejected* and
      *History can be replayed to a point in the past*. Verify: the snippets compile against
      `IAggregationService`.
- [ ] 3.4 `docs/guides/write-a-projection.md`: *A handler may take the event payload or the enveloped
      event*, *A replay reports progress and failure*, *A replayed event is applied under the session
      that produced it*. Verify: a snippet with a bare-payload handler compiles.
- [ ] 3.5 `docs/guides/write-a-saga.md`: *A handler may take the event payload or the enveloped event* and
      a link for *Saga processing is measured*. Verify: a snippet with a bare-payload saga handler
      compiles.
- [ ] 3.6 `docs/guides/outbox-setup-rabbitmq.md`: *The lock is released only by its holder*. Verify: a read
      against its two scenarios, including the unavailable lock service.
- [ ] 3.7 `docs/guides/encrypt-data-setup.md`: *Unreadable encrypted fields degrade rather than fail*, *One
      key store serves several processes over shared storage*, *Revoking a version destroys exactly that
      version*. Verify: a read against the three requirements' scenarios.
- [ ] 3.8 `docs/overview/packages.md`: *A consumer can step into the framework's source*. Verify: a read
      against the requirement's scenarios.
- [ ] 3.9 `docs/guides/tenant-membership.md`: *Tenants themselves are event-sourced*. Verify: the type-name
      test green for the tenant aggregate and events the section names.
- [ ] 3.10 `docs/guides/enforce-tenant-isolation.md`: *Tenant-scoped rows are filtered at the database as
      well as at the entrance*, with how to apply the filters. Verify: the snippet compiles.
- [ ] 3.11 `docs/guides/testing-patterns.md`: *The host makes what happened observable*. Verify: the snippet
      compiles against `EventStoreTestHost`.
- [ ] 3.12 `docs/guides/external-login-oidc.md`: *Identity messages are localisable*. Verify: a read against
      the requirement's scenarios.
- [ ] 3.13 Each page that gains a capability names it in its derivation header. Verify: a read of the twelve
      headers.

## 4. Close

- [ ] 4.1 `llms.txt` links the three new pages. Verify: `AiIndexTests` green.
- [ ] 4.2 `./scripts/local-gauntlet.sh` green; `openspec validate --strict` green; `docfx build` with warnings
      as errors green. Verify: the gauntlet's last line and the docfx exit code.
