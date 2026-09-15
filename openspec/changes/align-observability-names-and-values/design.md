## Context

See `proposal.md` → *Why*. Evidence read on 2026-09-14:

- `EventSource.cs:258-259` tags `events.appended` with `@event.GetType().Name` and
  `typeof(TAggregate).Name`; `EventSource.cs:172` tags the conflict counter with the stored
  `aggregateTypeName`, which is assembly-qualified.
- `ProjectionWorker.cs:138` and `SagaWorker.cs:139` tag `event.type` with `EventTypeName` from the
  bundle, which `EventBundleMapper` fills from `GetQualifiedTypeName()`.
- `ApplicationDiagnostics.Metrics` publishes each instrument as a `public static readonly` field; the
  name is reachable only through `.Name`.
- `OpenTelemetryExtensions.cs:94` subscribes with the literal `"Stratara.Service"`.
- `BackgroundTaskQueue` XML says the status-retention limit equals "the channel capacity for a typical
  host"; `AddBackgroundTasks()` sets capacity 100 and retention is 10 000.

## Goals / Non-Goals

**Goals:**

- One value form per type tag across all instruments.
- A constant for every instrument name.

**Non-Goals:**

- Renaming any instrument or tag.
- Consumer lag, new instruments, or changing what is counted.
- Making the background queue's limits configurable.

## Decisions

### D1 — The simple type name, everywhere

The projection, saga and conflict series move to the simple name the appended-events series and the
XML documentation already use. Owner decision 2026-09-14, from three options.

*Rejected: the assembly-qualified name everywhere.* It is unambiguous, but long, it changes with every
assembly version so a dashboard breaks on each upgrade, and it would move the series that already
match the documentation.

*Rejected: documenting the difference.* It leaves the join impossible, which is the problem.

Consequence: two event types with the same simple name in different namespaces share a tag value.
That is accepted — the same holds on `events.appended` today, and a consumer that needs to tell them
apart names its types apart.

Evidence: the code locations under *Context*; the owner's answer.

### D2 — Instrument names as `const string` beside the instruments

Each instrument gets a name constant next to its field, and the field is created from the constant, so
the two cannot drift. Additive surface in `Stratara.Diagnostics`.

*Rejected: rewording the requirement to "published as instruments".* A query language needs the
string, not the instrument object.

Evidence: `ApplicationDiagnostics.cs` instrument fields.

### D3 — Minor release

A changed emitted value is observable even though no name changes, so it ships in a minor version with
a CHANGELOG entry under *Changed* that names the three series. Owner decision 2026-09-14.

## Risks / Trade-offs

- **[A consumer alert filters on the qualified value and goes silent]** → The CHANGELOG entry names
  the three series and the old and new form; the documentation page states the form.
- **[Simple-name collisions across namespaces]** → Accepted per D1; documented on the page.

## Migration Plan

Consumers update filters on `projection.events.processed`, `saga.events.processed` and
`event_source.append.conflicts` from the assembly-qualified to the simple type name when they adopt
the minor version. No data migration.
