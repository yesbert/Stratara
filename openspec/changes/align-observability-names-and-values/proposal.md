# Align observability names and values

> **Status:** approved (owner, 2026-09-15)

## Why

A consumer builds dashboards on the framework's telemetry by name and joins series by tag value. Two
things stand in the way today. The `event.type` and `aggregate.type` tags carry the simple type name
on `event_source.events.appended` but the assembly-qualified name on `projection.events.processed`,
`saga.events.processed` and `event_source.append.conflicts`, so the same event or aggregate shows up
as two unrelated values and no dashboard can join the write side to the read side. And the
`observability` capability promises every instrument name as a published constant, while only the
instruments themselves are published — a consumer who wants the name in a query has to copy a
literal. The documentation review of 2026-09-14 found both, plus two pieces of wording that do not
match what ships.

## What Changes

- **Every `event.type` and `aggregate.type` tag value is the simple type name**, on every instrument.
  The projection, saga and conflict series change value from the assembly-qualified name to the
  simple name. A dashboard or alert filtering those series on the old value needs its filter updated;
  names of instruments and tags do not change. Recorded in the CHANGELOG as a changed observable value.
- **Every instrument name is published as a constant**, alongside the existing source, meter, tag and
  outcome constants. Additive.
- The requirement on pipeline measurements says an in-flight gauge is not dimensioned by outcome,
  since work in flight has none yet.
- Two code corrections with no observable effect: the default telemetry wiring subscribes to the meter
  through its published constant instead of a literal, and the background task queue's documentation
  states its real capacity.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `observability`: *Instrument names are a stable published contract* (instrument names join the
  published constants); *The framework measures throughput and latency across the event pipeline*
  (one value form per type tag; in-flight gauge without outcome).

## Impact

- `Stratara.Diagnostics` — new public constants for the instrument names.
- `Stratara.Infrastructure` (conflict counter), `Stratara.Projections`, `Stratara.Sagas` — the type tag
  values they emit.
- `Stratara.ServiceDefaults` — meter subscription through the constant.
- `Stratara.Infrastructure` background task queue — XML documentation only.
- `docs/guides/observe-the-framework.md` — the tag value form and the instrument-name constants.
- Round-4 tracker entries R4-Arc-011, R4-Arc-012, R4-Arc-013, R4-Arc-014, closed by this change.
- Release: the changed tag values ship in a minor version, not a patch — owner decision 2026-09-14.
