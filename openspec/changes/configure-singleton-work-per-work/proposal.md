# configure-singleton-work-per-work

> **Status:** proposed

## Why

`AddStrataraSingletonWork<TWork>(configure)` takes a settings callback per work, and every callback
configures one silo-wide `SingletonWorkOptions`
(`OrleansSingletonWorkServiceCollectionExtensions.cs:68-72`). Two works registered with keep-alive
periods of two and five minutes both run with five — whichever callback ran last. The signature promises
something the framework does not do. A second registration of the same work also adds its callback a
second time, which `close-the-adoption-paths-last-gaps` (D3) left open as "a change with a question in
it". The owner answered the question (2026-09-18): settings are per work.

## What Changes

- **A registration's settings apply to its work alone.** Each work's settings are the host's silo-wide
  `SingletonWorkOptions` (what `services.Configure<SingletonWorkOptions>(…)` sets) with that work's own
  callback applied on top.
- **A work registered twice keeps the later registration's callback**, once — the composition of two
  calls is the composition of the last.
- **A host that configured `SingletonWorkOptions` directly keeps that as the default** for every work;
  nothing changes for it. A host that relied on one work's callback configuring the others — the
  behaviour this removes — sets the value silo-wide instead; the CHANGELOG says so.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: *Work that must happen once happens once per cluster* — settings per work, the
  host's as the default, the later of two registrations.

## Impact

- Affected code: `src/Stratara.Orleans/DependencyInjection/OrleansSingletonWorkServiceCollectionExtensions.cs`,
  `src/Stratara.Orleans/Singleton/SingletonWorkGrain.cs`, the internal `SingletonWorkRegistrations`
- Tests: `tests/Stratara.Orleans.Tests` (settings per work, twice-registered), the idempotency test of
  `close-the-adoption-paths-last-gaps` now includes the callback
- Affected docs: `docs/guides/operate-the-orleans-execution-model.md` (singleton work), XML docs of both
  overloads, `CHANGELOG.md`
- Superseded: `openspec/changes/archive/2026-09-18-close-the-adoption-paths-last-gaps/design.md` D3.
- Public API: none (signatures unchanged; the documented meaning of `configure` narrows to its work).
- Schema: none. Versioning: part of 4.2.0.
