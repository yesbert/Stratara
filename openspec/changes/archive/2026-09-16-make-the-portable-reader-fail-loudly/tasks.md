## 0. Gate

- [x] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.
      Done: approved by the owner on 2026-09-16, recorded at the owner's request.

## 1. An unpositioned entry stops its partition (D1)

- [x] 1.1 `PortableCounterReader.ReadAfterAsync` looks up a bounded number of unpositioned entries through
      the position index and throws, naming the entry, `PartitionCounterInterceptor` and
      `PartitionCounterBackfill`, when one belongs to its partition. Verify:
      `src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PortableCounterReader.cs`.
      Done: `RefuseUnpositionedAsync`, probe of 64 through the index, partition derived with `PartitionMap`.
- [x] 1.2 An integration test on PostgreSQL: a store read by the portable reader, an append through a context
      without the interceptor; the read of that partition throws with the message, reads of other
      partitions do not, and after `PartitionCounterBackfill.RunAsync` the entry is read. Run it against the
      current reader first and record that it fails there: no exception, and the entry is never returned. Verify:
      a test in `tests/Stratara.Orleans.IntegrationTests/CommitOrder`.
      Done: `PortableReaderFailsLoudlyTests.A_read_stops_at_an_entry_appended_without_a_position_until_it_is_positioned`;
      against the 4.1.1 reader "Assert.Throws() Failure: No exception was thrown".
- [x] 1.3 The query plan uses the position index on PostgreSQL. Verify: `EXPLAIN` of the generated statement
      in the test's output or recorded here.
      Done: with `enable_seqscan = off` (a small test table is scanned otherwise) PostgreSQL plans `Limit -> Index Scan using ix_event_stream_entry_partition_position`, `Index Cond: (partition_position IS NULL)` for `SELECT sequence_number, bucket_id FROM event_stream_entry WHERE partition_position IS NULL LIMIT 64`. Checked with a throwaway test, not committed.

## 2. A lowered partition count refuses to start (D2)

- [x] 2.1 `PortableCounterStartupCheck` refuses a counter row at or above `PartitionCount`, naming both.
      Verify: `src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PortableCounterStartupCheck.cs`.
      Done: highest counter row compared with `CommitOrderOptions.PartitionCount`; the check takes the options.
- [x] 2.2 An integration test on PostgreSQL: counter rows for 8 partitions, a host configured with 4 fails at
      start with the message; configured with 8 it starts. Verify: a test in
      `tests/Stratara.Orleans.IntegrationTests/CommitOrder`.
      Done: `PortableReaderFailsLoudlyTests.A_host_with_a_partition_count_lower_than_the_stores_counters_refuses_to_start`.

## 3. The interceptor cleans up (D3)

- [x] 3.1 `PartitionCounterInterceptor` rolls back, disposes and forgets its transaction when positioning
      throws and when the commit throws, and replaces a stale entry. Verify:
      `src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PartitionCounterInterceptor.cs`.
      Done: positioning wrapped, `ReleaseAsync` commits or rolls back and always disposes and forgets; `AddOrUpdate`.
- [x] 3.2 An integration test on PostgreSQL: a save that fails while positioning (no counter row for the
      partition) followed by seeding the row and saving again on the same context succeeds. Run it against
      the current interceptor first and record the failure there. Verify: a test in
      `tests/Stratara.Orleans.IntegrationTests/CommitOrder`.
      Done: `PortableReaderFailsLoudlyTests.A_save_whose_positioning_failed_leaves_no_transaction_for_the_next_save_on_the_context`
      passes. Against the 4.1.1 interceptor it passes as well — the review's failure does not reproduce (design D3,
      *Found during apply*); the test stays as the regression guard for the hardening.

## 4. Documentation (D4)

- [x] 4.1 XML documentation of `CommitOrderOptions.MaintainPartitionCounter` and `PartitionCount`,
      `AddStrataraPortableCounterReader` and `PortableCounterReader`: every appending process needs the
      interceptor, the framework does not read `MaintainPartitionCounter`, the count does not change without
      renumbering, verified on PostgreSQL only. Verify: the files; documentation tests.
      Done: `CommitOrderOptions` remarks on both properties, `AddStrataraPortableCounterReader`, `PortableCounterReader` remarks and `<exception>`. Documentation tests 699/699.
- [x] 4.2 `docs/guides/migrate-to-the-orleans-execution-model.md` → *Choose a commit-order reader* and
      `docs/guides/operate-the-orleans-execution-model.md` → *A partition that stops advancing* say the same
      and what the new stop and the start refusal mean. `llms.txt` qualifies the portable counter. Verify: a
      read of each; documentation tests.
      Done: the migration guide's portable-reader paragraph, the operations guide's *A partition that stops advancing*, the `Stratara.Orleans.EntityFrameworkCore` line of `llms.txt`. Documentation tests 699/699.

## 5. Close

- [x] 5.1 `CHANGELOG.md` `[Unreleased]` → *Fixed*: the reader stops instead of skipping, the start check
      refuses a lowered count, the interceptor cleans up; *Changed*: the documented preconditions. Verify:
      the entries.
      Done: *Changed*: preconditions; *Fixed*: stop at unpositioned entry, start refusal, interceptor transaction.
- [x] 5.2 `./scripts/local-gauntlet.sh` green; the `CommitOrder` and `Projections` integration namespaces
      green against PostgreSQL. Verify: the run output, recorded here.
      Done 2026-09-16: gauntlet green; `Projections` 8/8; `CommitOrder` 22/22. The first `CommitOrder` run failed
      once — `InterleavedCommitTests.Randomised_interleavings(portable-counter)` stopped at an unpositioned entry —
      because that class ran all four readers against one database and the other three append without the
      counter. The new stop working as specified; the class now gives the counted case a database of its own.
- [x] 5.3 `openspec validate make-the-portable-reader-fail-loudly --strict` passes. Verify: the output.
      Done: "Change 'make-the-portable-reader-fail-loudly' is valid".
