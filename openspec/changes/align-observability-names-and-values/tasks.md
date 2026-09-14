## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. Tag values (D1, D3)

- [ ] 1.1 The conflict counter tags `aggregate.type` with the simple aggregate type name. Verify: a test
      in `tests/Stratara.Infrastructure.Tests` with a `MeterListener` that records the tag value on a
      forced conflict.
- [ ] 1.2 The projection and saga workers tag `event.type` with the simple event type name. Verify:
      tests in `tests/Stratara.Projections.Tests` and `tests/Stratara.Sagas.Tests` that record the tag
      value for a processed bundle.
- [ ] 1.3 CHANGELOG `[Unreleased]` → *Changed* names the three series with the old and new value form.
      Verify: the entry.

## 2. Names (D2)

- [ ] 2.1 Every instrument on `ApplicationDiagnostics.Metrics` has a public name constant with XML
      documentation, and the instrument is created from it. Verify: a test in
      `tests/Stratara.Diagnostics.Tests` (or the nearest existing diagnostics test project) that asserts
      each instrument's `Name` equals its constant.
- [ ] 2.2 `ConfigureOpenTelemetry` subscribes to the meter through `ApplicationDiagnostics.Metrics.MeterName`.
      Verify: `grep -n '"Stratara.Service"' src/Stratara.ServiceDefaults` returns nothing.

## 3. Wording

- [ ] 3.1 `BackgroundTaskQueue` XML states capacity 100 and retention 10 000 correctly. Verify: a read of
      the file.
- [ ] 3.2 `docs/guides/observe-the-framework.md` states the simple-name form for type tags and lists the
      instrument-name constants. Verify: `tests/Stratara.Documentation.Tests` green, including the inline
      type-name test.
- [ ] 3.3 `llms-full.txt` regenerated. Verify: `ReferenceCatalogueIsCurrentTests` green.

## 4. Close

- [ ] 4.1 `./scripts/local-gauntlet.sh` green; `openspec validate --strict` green; `/bump-version minor`
      queued with the next release decision. Verify: the gauntlet's last line.
- [ ] 4.2 Round-4 tracker entries R4-Arc-011 to R4-Arc-014 closed with the merge commit. Verify: the
      tracker.
