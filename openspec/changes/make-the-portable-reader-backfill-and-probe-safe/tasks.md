## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. The backfill appends after the counter (D1)

- [ ] 1.1 A failing test on today's code: positioned 1..10 with a checkpoint at 10, a foreign append, the
      backfill, a read after 10 — today returns the entry that was at 10 and never the foreign one.
      Verify: `tests/Stratara.Orleans.IntegrationTests/CommitOrder/PortableReaderFailsLoudlyTests.cs`
      (scenario *A late entry is positioned behind a checkpoint*); record the failure.
- [ ] 1.2 `PartitionCounterBackfill.RunPartitionAsync` hands the unpositioned entries `counter + 1 …` in
      sequence order, sets the counter to `counter + n`, and touches no positioned entry; the shift
      statement goes; summary and remarks name the window and the remedy, and drop "must be reset".
      Verify: `src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PartitionCounterBackfill.cs`; 1.1
      green; a second run still returns 0.
- [ ] 1.3 `PartitionCounterBackfillTests` expects positioned-first, late-after, with the comment updated;
      the history-only path (counter at 0) still reads history in sequence order (scenario *A store with
      history adopts the portable reader*). Verify: the test file; both assertions green.

## 2. The probe is per partition and complete (D2)

- [ ] 2.1 A failing test on today's code: one foreign append to the reader's partition, then more foreign
      appends to other partitions than one read returns; the read passes the entry. Verify:
      `PortableReaderFailsLoudlyTests.cs` (scenario *Unpositioned entries pile up in other partitions*);
      record the failure.
- [ ] 2.2 `PortableCounterReader.RefuseUnpositionedAsync` is one ordered query filtered by partition; the
      constant goes; `HeadAsync` shares it. Verify: `PortableCounterReader.cs:29-30,82-96`; 2.1 green;
      `ReaderHeadTests` green.

## 3. Positions follow the stream's version order (D3)

- [ ] 3.1 A test: one save with versions 3, 1, 2 of one stream added in that order beside a second stream;
      the positions read back follow 1, 2, 3 within the stream. Verify: a new test in
      `tests/Stratara.Orleans.IntegrationTests/CommitOrder` (scenario *One append holds several versions
      of one stream*); run against today's interceptor first and record whether it passes by accident.
- [ ] 3.2 `PartitionCounterInterceptor.SavingChangesAsync` orders the added entries by the stream's first
      appearance, then by version, before grouping. Verify: `PartitionCounterInterceptor.cs:45-63`; 3.1
      green; `InterleavedCommitTests` green for the portable reader.

## 4. The switch is obsolete (D4)

- [ ] 4.1 `CommitOrderOptions.MaintainPartitionCounter` carries `[Obsolete]` with the message naming the
      interceptor; `SurfaceTests` lists it so. Verify: `src/Stratara.Orleans/CommitOrder/CommitOrderOptions.cs`;
      `tests/Stratara.Orleans.Tests/SurfaceTests.cs`.
- [ ] 4.2 `PocCommitOrderWriteDbContext` reads a switch of the test store's own; every configuration that set
      the option in `tests/Stratara.Orleans.IntegrationTests`, `tests/Stratara.Orleans.Scenarios` and
      `tests/Stratara.Orleans.Benchmarks` sets that switch instead; the build is warning-free. Verify:
      `grep -rn MaintainPartitionCounter tests` finds only the surface test; the gauntlet's build step.

## 5. Documentation

- [ ] 5.1 `docs/guides/migrate-to-the-orleans-execution-model.md` — *Choose a commit-order reader*: the
      backfill appends after the counter, the window and the remedy, the retired switch; the sentence
      about resetting a checkpoint after a backfill goes; the settings table drops the switch.
      `docs/guides/operate-the-orleans-execution-model.md` — *A partition that stops advancing*: the
      order after positioning and when to rebuild. Verify: the sections; documentation tests.
- [ ] 5.2 `src/Stratara.Orleans.EntityFrameworkCore/README.md`, `CHANGELOG.md` `[Unreleased]` *Fixed* (the
      backfill, the probe, the stamp order) and *Deprecated* (the switch), `llms.txt`. Verify: the entries;
      doc-symbol check.

## 6. Close

- [ ] 6.1 `./scripts/local-gauntlet.sh` green; the `CommitOrder` integration namespace green against
      PostgreSQL. Verify: the run output, recorded here.
- [ ] 6.2 `openspec validate make-the-portable-reader-backfill-and-probe-safe --strict` passes. Verify: the
      output.
