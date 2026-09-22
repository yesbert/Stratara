## 1. The native reader reads under any mapping

- [x] 1.1 In `PostgresTransactionIdReader.Statements.For`, build a projection list from the entity
  type's mapped properties with the `Column(...)` helper already there, and replace the `SELECT *`
  of both read statements (`PostgresTransactionIdReader.cs:155` and `:163`) with it. The head
  statement is unaffected — it selects one aggregate.
- [x] 1.2 Add a write context to `tests/Stratara.Orleans.IntegrationTests/Store/` that applies
  `ApplyRowVersionConvention(RowVersionMode.Uint)`, alongside the existing `PascalCaseWriteDbContext`.
- [x] 1.3 Add `CommitOrder/NativeReaderRowVersionTests.cs`: append to a store on that context, drain
  the native reader, and assert the entries are returned. Without task 1.1 it fails with
  `42703: column s.xmin does not exist`.
- [x] 1.4 Run `dotnet test tests/Stratara.Orleans.IntegrationTests` (needs Docker) and confirm 1.3
  passes and `NativeReaderModelNamesTests` still does.

## 2. Entries of one commit are read in stream order

- [x] 2.1 In `PostgresTransactionIdReader.ReadAfterAsync`, order the rows of one transaction the way
  `PartitionCounterInterceptor.InStreamOrder` orders the entries of one save — streams by first
  appearance, then by version — after the existing `OrderBy(TransactionId)`, replacing the
  `ThenBy(SequenceNumber)` within a transaction.
- [x] 2.2 Apply the same order in `ReadWholeTransactionAsync`, whose statement orders by the
  sequence column today.
- [x] 2.3 Keep the batch cut correct: the cut looks for the first row of the last transaction, so it
  must run on the transaction grouping, not on the sequence order. Verified by
  `WholeTransactionReadTests`.
- [x] 2.4 Add `CommitOrder/CommitStreamOrderTests.cs`: one save that creates a stream and appends a
  second version of it, plus a second stream in the same commit; assert each stream's entries arrive
  in version order under the native reader. Cover the portable reader in the same test so the
  reader-neutral requirement is pinned on both.
- [x] 2.5 Run `dotnet test tests/Stratara.Orleans.IntegrationTests` and confirm
  `InterleavedCommitTests`, `WholeTransactionReadTests` and `ReaderHeadTests` still pass.

## 3. An append without a causation identity is refused

- [x] 3.1 In `EventSource`, refuse a session whose causation identity is absent where the missing
  session is refused today — in `PrepareEventBundle`, before the transaction opens — with a message
  naming `AddCommandAuditing()`.
- [x] 3.2 Choose the exception type to match what the missing session throws (`SessionRequiredException`)
  and document it on the throwing member; CS1591 is an error in this project.
- [x] 3.3 Add a unit test in `tests/Stratara.Infrastructure.Tests` asserting that a save with a
  session carrying no causation identity throws before anything is written, and that the message
  names `AddCommandAuditing()`.
- [x] 3.4 Check the existing suites for a session built without a causation identity and give those
  fixtures one — `tests/Stratara.Infrastructure.Tests`, `tests/Stratara.WriteStore.Tests`,
  `tests/Stratara.Orleans.Tests` and the testing packages are the likely places.

## 4. Documentation

- [x] 4.1 In `docs/guides/write-a-command-handler.md`, state that appending requires
  `AddCommandAuditing()` and what the host gets without it.
- [x] 4.2 In `docs/concepts/session-context.md`, fix the system-flow example (around line 108), which
  builds a session with `CausationId: null` and would now be refused.
- [x] 4.3 In `docs/guides/write-a-projection.md`, state what a projection may rely on within one
  commit: each stream's entries in version order, streams interleaved in any order.
- [x] 4.4 Add the three entries to `CHANGELOG.md` under Unreleased, the reader defect first.

## 5. Gate

- [x] 5.1 `openspec validate read-what-was-written --strict`
- [x] 5.2 `./scripts/local-gauntlet.sh`
- [x] 5.3 `dotnet test tests/Stratara.Orleans.IntegrationTests` (Docker; the gauntlet skips it)
