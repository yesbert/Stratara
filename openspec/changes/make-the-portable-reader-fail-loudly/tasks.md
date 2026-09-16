## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. An unpositioned entry stops its partition (D1)

- [ ] 1.1 `PortableCounterReader.ReadAfterAsync` looks up a bounded number of unpositioned entries through
      the position index and throws, naming the entry, `PartitionCounterInterceptor` and
      `PartitionCounterBackfill`, when one belongs to its partition. Verify:
      `src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PortableCounterReader.cs`.
- [ ] 1.2 An integration test on PostgreSQL: a store read by the portable reader, an append through a context
      without the interceptor; the read of that partition throws with the message, reads of other
      partitions do not, and after `PartitionCounterBackfill.RunAsync` the entry is read. Run it against the
      current reader first and record that it fails there: no exception, and the entry is never returned. Verify:
      a test in `tests/Stratara.Orleans.IntegrationTests/CommitOrder`.
- [ ] 1.3 The query plan uses the position index on PostgreSQL. Verify: `EXPLAIN` of the generated statement
      in the test's output or recorded here.

## 2. A lowered partition count refuses to start (D2)

- [ ] 2.1 `PortableCounterStartupCheck` refuses a counter row at or above `PartitionCount`, naming both.
      Verify: `src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PortableCounterStartupCheck.cs`.
- [ ] 2.2 An integration test on PostgreSQL: counter rows for 8 partitions, a host configured with 4 fails at
      start with the message; configured with 8 it starts. Verify: a test in
      `tests/Stratara.Orleans.IntegrationTests/CommitOrder`.

## 3. The interceptor cleans up (D3)

- [ ] 3.1 `PartitionCounterInterceptor` rolls back, disposes and forgets its transaction when positioning
      throws and when the commit throws, and replaces a stale entry. Verify:
      `src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PartitionCounterInterceptor.cs`.
- [ ] 3.2 An integration test on PostgreSQL: a save that fails while positioning (no counter row for the
      partition) followed by seeding the row and saving again on the same context succeeds. Run it against
      the current interceptor first and record the failure there. Verify: a test in
      `tests/Stratara.Orleans.IntegrationTests/CommitOrder`.

## 4. Documentation (D4)

- [ ] 4.1 XML documentation of `CommitOrderOptions.MaintainPartitionCounter` and `PartitionCount`,
      `AddStrataraPortableCounterReader` and `PortableCounterReader`: every appending process needs the
      interceptor, the framework does not read `MaintainPartitionCounter`, the count does not change without
      renumbering, verified on PostgreSQL only. Verify: the files; documentation tests.
- [ ] 4.2 `docs/guides/migrate-to-the-orleans-execution-model.md` → *Choose a commit-order reader* and
      `docs/guides/operate-the-orleans-execution-model.md` → *A partition that stops advancing* say the same
      and what the new stop and the start refusal mean. `llms.txt` qualifies the portable counter. Verify: a
      read of each; documentation tests.

## 5. Close

- [ ] 5.1 `CHANGELOG.md` `[Unreleased]` → *Fixed*: the reader stops instead of skipping, the start check
      refuses a lowered count, the interceptor cleans up; *Changed*: the documented preconditions. Verify:
      the entries.
- [ ] 5.2 `./scripts/local-gauntlet.sh` green; the `CommitOrder` and `Projections` integration namespaces
      green against PostgreSQL. Verify: the run output, recorded here.
- [ ] 5.3 `openspec validate make-the-portable-reader-fail-loudly --strict` passes. Verify: the output.
