# Document every capability

> **Status:** approved (owner, 2026-09-14)

## Why

A review of the documentation site on 2026-09-14 mapped every requirement under `openspec/specs/`
onto `docs/`. Fourteen capabilities have requirements a consumer can observe that no page describes,
and one of them — `session-context`, the model every tenant check, audit record and encrypted field
rests on — has no page at all. The same review found two ways the documentation tests pass on pages
that do not say what the tests believe: a configuration section counts as documented when only a
member of a type with the same name is mentioned, and a type name in prose is never checked against
the assemblies — which is how `SagaOrchestrationWorker`, `EventProjectionWorker` and
`OptimisticConcurrencyException` stayed on the site for releases.

**Consumer-visible effect:** the documentation site gains three pages and fills fourteen gaps on
existing ones. No published behaviour, API, package or requirement changes; the test changes are in a
test project that is not packable.

## What Changes

**New pages**

| Page | From capability | What it covers |
|---|---|---|
| `docs/concepts/session-context.md` | `session-context` | Actor and subject, the connection identity, the ambient and settable context, how an authenticated request populates it from claims, why tenant resolution fails closed, the `SessionContext` configuration section with `AllowTenantHeader`, the trace stamp, correlation for a request that supplies none, the ambient identities readable without the context |
| `docs/guides/observe-the-framework.md` | `observability` | One source and one meter, the instrument names as a published contract and the constants that carry them, every instrument including the concurrency-conflict counter and the saga instruments, redaction of sensitive request headers, why protected field values never reach a log message; links to the log-event schema |
| `docs/guides/queue-background-work.md` | `host-composition` | Queueing work for in-process execution, the outcome it reports, bounded status retention, parallel execution in entry order |

**Additions to existing pages**

| Page | From capability | Requirement it adds |
|---|---|---|
| `docs/guides/write-a-command-handler.md` | `event-sourcing-store` | *The owning tenant is resolved from the stream before the session*; *A concurrency conflict discards the batch and is distinguishable*, in full |
| `docs/reference/di-extensions-cheatsheet.md` and a new section in `docs/getting-started/di-composition.md` | `event-sourcing-store` | *The store declares its own schema*: the constraints a consumer's migration must carry, and why a context that shares an assembly with its siblings filters its configurations |
| `docs/guides/configure-snapshots.md` | `aggregate-rehydration` | *An unhandled event is skipped rather than rejected*; *History can be replayed to a point in the past* |
| `docs/guides/write-a-projection.md` | `projections` | *A handler may take the event payload or the enveloped event*; *A replay reports progress and failure*; *A replayed event is applied under the session that produced it* |
| `docs/guides/write-a-saga.md` | `sagas` | *A handler may take the event payload or the enveloped event*; *Saga processing is measured* (link to the observability page) |
| `docs/guides/outbox-setup-rabbitmq.md` | `outbox-and-messaging` | *The lock is released only by its holder*, including a lock service that is unavailable counting as not acquired |
| `docs/guides/encrypt-data-setup.md` | `data-encryption` | *Unreadable encrypted fields degrade rather than fail*; *One key store serves several processes over shared storage*; *Revoking a version destroys exactly that version*, in full |
| `docs/overview/packages.md` | `package-distribution` | *A consumer can step into the framework's source* |
| `docs/guides/tenant-membership.md` | `tenant-directory` | *Tenants themselves are event-sourced* |
| `docs/guides/enforce-tenant-isolation.md` | `tenant-isolation` | *Tenant-scoped rows are filtered at the database as well as at the entrance*, with the guidance a consumer needs to apply the filters |
| `docs/guides/testing-patterns.md` | `test-support` | *The host makes what happened observable* |
| `docs/guides/external-login-oidc.md` | `external-identity` | *Identity messages are localisable* |

**Documentation tests** (`tests/Stratara.Documentation.Tests`)

- A section name followed by a member access no longer counts as naming the section, so the
  `SessionContext` section is reported as undocumented until the new page names it.
- A new test checks the framework-shaped type names a page writes in inline code against the
  published assemblies, so a type that does not exist fails the build instead of shipping.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

None. Every page is derived from a requirement that already exists; no requirement changes. The
change sets `skip_specs: true`.

## Impact

- **Documentation:** the three new pages and the twelve pages above; the `toc.yml` of each section
  that gains a page; the landing page only if a new page belongs among its entry points.
- **Tests:** `tests/Stratara.Documentation.Tests/DocumentationCorpus.cs` (the token boundary) and one
  new test class for inline type names, with its allowlist.
- **Not touched:** `src/`, every package, every spec, `llms.txt` beyond links to the new pages,
  `llms-full.txt` (generated).
- **Version:** no bump; the site deploys on merge through `deploy-site.yml`, behind its approval.
- **Superseded sources:** none.
