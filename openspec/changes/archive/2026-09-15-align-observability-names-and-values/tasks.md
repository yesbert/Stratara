## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.
      Done: approved by the owner on 2026-09-15, recorded at the owner's request.

## 1. Tag values (D1, D3)

- [x] 1.1 The conflict counter tags `aggregate.type` with the simple aggregate type name. Verify: a test
      in `tests/Stratara.Infrastructure.Tests` with a `MeterListener` that records the tag value on a
      forced conflict.
      Done: `EventSource` tags the stored qualified name through `ApplicationDiagnostics.MetricTags.TypeNameValue`,
      the one conversion every type tag uses. `EventSourceConflictMetricsTests` forces a conflict on the SQLite test
      host and finds the conflict measurement tagged with the nested probe type's simple name; its theory covers
      simple, namespace-qualified, assembly-qualified, versioned, nested and generic names. Infrastructure tests
      355/355.
- [x] 1.2 The projection and saga workers tag `event.type` with the simple event type name. Verify:
      tests in `tests/Stratara.Projections.Tests` and `tests/Stratara.Sagas.Tests` that record the tag
      value for a processed bundle.
      Done: both workers tag through `TypeNameValue`. `ProjectionWorkerMetricsTests` and `SagaWorkerMetricsTests` now
      send an assembly-qualified event type name and assert the simple name on the success and the failure
      measurement. Projections 103/103, Sagas 46/46.
- [x] 1.3 CHANGELOG `[Unreleased]` → *Changed* names the three series with the old and new value form.
      Verify: the entry.
      Done: the *Changed* entry names `projection.events.processed`, `saga.events.processed` and
      `event_source.append.conflicts` with an example of each form; *Added* lists the name constants and
      `TypeNameValue`.

## 2. Names (D2)

- [x] 2.1 Every instrument on `ApplicationDiagnostics.Metrics` has a public name constant with XML
      documentation, and the instrument is created from it. Verify: a test in
      `tests/Stratara.Diagnostics.Tests` (or the nearest existing diagnostics test project) that asserts
      each instrument's `Name` equals its constant.
      Done: ten constants join the Orleans ones that already followed the pattern, and all eighteen instruments
      are created from their constant. There is no diagnostics test project; the nearest is
      `ObservabilityMetricsTests` in `tests/Stratara.Testing.EntityFrameworkCore.Tests`, whose new test pairs every
      constant with its instrument's `Name`. 30/30.
- [x] 2.2 `ConfigureOpenTelemetry` subscribes to the meter through `ApplicationDiagnostics.Metrics.MeterName`.
      Verify: `grep -n '"Stratara.Service"' src/Stratara.ServiceDefaults` returns nothing.
      Done: the grep finds the literal only in generated documentation files under `bin/`, which copy the XML
      comment on `MeterName`; no source file under `src/Stratara.ServiceDefaults` contains it. ServiceDefaults tests
      17/17.

## 3. Wording

- [x] 3.1 `BackgroundTaskQueue` XML states capacity 100 and retention 10 000 correctly. Verify: a read of
      the file.
      Done: the remarks say the retention default is 10 000 entries and that `AddBackgroundTasks()` sets the
      channel capacity to 100.
- [x] 3.2 `docs/guides/observe-the-framework.md` states the simple-name form for type tags and lists the
      instrument-name constants. Verify: `tests/Stratara.Documentation.Tests` green, including the inline
      type-name test.
      Done: the names section describes the `…Name` constant beside each instrument, and the tag table gives the
      simple-name form for `aggregate.type` and `event.type`, the shared value for same-named types, and
      `TypeNameValue`. Documentation tests 699/699.
- [x] 3.3 `llms-full.txt` regenerated. Verify: `ReferenceCatalogueIsCurrentTests` green.
      Done: regenerated with `dotnet run --project tools/Stratara.ReferenceCatalogue -- llms-full.txt`; the
      catalogue carries registrations, options and exceptions, so the file is unchanged, and the test is green.

## 4. Close

- [x] 4.1 `./scripts/local-gauntlet.sh` green; `openspec validate --strict` green; `/bump-version minor`
      queued with the next release decision. Verify: the gauntlet's last line.
      Done: gauntlet and change validation green before #97; the owner chose on 2026-09-15 to ship this change
      in 4.1.0 with the Orleans execution model, and the bump branch carries it.
- [x] 4.2 Round-4 tracker entries R4-Arc-011 to R4-Arc-014 closed with the merge commit. Verify: the
      tracker.
      Done: closed with `139c5e2` (#97) on 2026-09-15.
