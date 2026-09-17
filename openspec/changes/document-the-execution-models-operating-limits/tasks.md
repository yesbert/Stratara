## 0. Gate

- [ ] 0.1 The owner has set this proposal's status line to `approved`. Verify: `proposal.md` line 3.
      Nothing below starts before it.

## 1. The caller side of the response timeout (D1)

- [ ] 1.1 A forwarded command whose handler outlasts the response timeout fails its caller with a
      timeout, runs once to the end and commits its append. Verify: an integration test beside
      `tests/Stratara.Orleans.IntegrationTests/Aggregates/LongOrderTests.cs` with
      `MessagingOptions.ResponseTimeout = 2 s` and one four-second handler (scenario *A forwarded
      command's handler outlasts the response timeout*): `TimeoutException` at the caller, one start and
      one completion, the aggregate's version incremented once.
- [ ] 1.2 `docs/guides/operate-the-orleans-execution-model.md` *The response timeout* says the handler
      runs to the end and commits after the caller's timeout, that a caller does not retry on a timeout,
      and lists the three ways out together. Verify: the section; documentation tests.

## 2. Authorization on the intent path (D2)

- [ ] 2.1 A role-guarded command dispatched under a session-driven provider is resumed after a kill and
      handled; the same command under a request-bound provider is kept after its attempts. Verify: two
      integration tests beside `tests/Stratara.Orleans.IntegrationTests/Aggregates/IntentPipelineTests.cs`
      (scenario *A resumed command is authorized from its recorded session*), the second asserting
      `attempts=3 kept=true` and a `117_112` per attempt.
- [ ] 2.2 The migration guide's dispatcher row and `docs/guides/require-permission.md` say a provider on
      the intent path answers from the session context and name `MembershipAuthorizationProvider` as
      one that does; the remark on `IAuthorizationProvider`'s example says its shape is the request side.
      Verify: the row, the guide section, `src/Stratara.Abstractions/Authorization/IAuthorizationProvider.cs`;
      documentation tests.

## 3. The record's transaction (D3)

- [ ] 3.1 A dispatch inside a unit of work the caller abandons without a save is recorded and runs.
      Verify: an integration test beside `IntentPipelineTests` (scenario *A caller's own unit of work
      fails after the dispatch*).
- [ ] 3.2 `docs/concepts/orleans-execution-model.md` *An accepted command is not lost* and the migration
      guide's dispatcher row say the record is committed on its own, and what a caller that needs its own
      writes and the command together does. Verify: both pages; documentation tests.

## 4. The two passes of a full replay (D4)

- [ ] 4.1 `ReplayWithStoreReadersTests` asserts that the unguarded total has counted each fact twice after
      the replay and the catch-up, and the guarded view once (scenario *A full replay runs on a host whose
      projections read the store*). Verify: the assertions in
      `tests/Stratara.Orleans.IntegrationTests/Projections/ReplayWithStoreReadersTests.cs`.
- [ ] 4.2 The migration guide's *A full replay on a host whose projections read the store* and the concept
      page's cost list say the store is applied twice, why that is correct, and that
      `IProjectionRebuilder.RebuildAsync` re-reads one read model once. Verify: both pages; documentation
      tests.

## 5. Singleton work under a suspected death (D5)

- [ ] 5.1 `ISingletonWork`'s summary says the work runs in one place while the cluster agrees on its
      membership, that a declared-dead silo may still run it until it learns of the declaration, and that
      a run tolerates an overlapping run elsewhere. Verify:
      `src/Stratara.Abstractions/Abstractions/Singleton/ISingletonWork.cs`; doc-symbol check.
- [ ] 5.2 The operations guide gains *Singleton work under a suspected death*: the sequence from missed
      probes to the declared silo's stop, each step with the setting that bounds it, the derived failover
      bound with the defaults, the overlap window, and the two-silo kill test's measurement under the
      test profile; the concept page's *Once per cluster* carries the qualification. Verify: the section
      and the paragraph; documentation tests.

## 6. The hybrid shape (D6)

- [ ] 6.1 `ReplaceBundleDispatcher` wraps the removed descriptor through `Instantiate` whatever its
      shape, and throws `InvalidOperationException` naming `hybrid` and `AddOutboxDispatcher` when
      `hybrid` is true and no `IEventBundleOutboxDispatcher` is registered. Verify:
      `src/Stratara.Orleans/DependencyInjection/OrleansProjectionServiceCollectionExtensions.cs`; unit
      tests in `tests/Stratara.Orleans.Tests` — a factory-registered dispatcher receives the bundle
      through the wrapper (scenario *A host keeps the bus beside the grains with a dispatcher registered
      by a factory*), and the missing case throws with both names (scenario *A host asks to keep the bus
      without a bus dispatcher*); run both against today's code first and record the silent outcome.
- [ ] 6.2 The migration guide's hybrid paragraph says what `hybrid: true` keeps and what a missing bus
      dispatcher does. Verify: the paragraph; documentation tests.

## 7. Package documentation (D7)

- [ ] 7.1 `src/Stratara.Orleans/GrainDirectories.cs` XML no longer says "proof of concept" and says what
      the durable directory is for. Verify: the file; doc-symbol check.
- [ ] 7.2 `src/Stratara.Orleans.EntityFrameworkCore/README.md` names `AddStrataraIntentStore`,
      `AddStrataraPortableCounterReader`, `AddStrataraExecutionModelReset` and `PartitionCounterBackfill`
      (verified, R5-Mig-012) and its quick start shows the write side. Verify: the table and the quick
      start; doc-symbol check.
- [ ] 7.3 `llms.txt` (regenerate `llms-full.txt`) and `CHANGELOG.md` `[Unreleased]`: *Fixed* for the
      hybrid wrapper, *Changed* for the refusal without a bus dispatcher and for the documented limits.
      Verify: the entries.

## 8. Close

- [ ] 8.1 `./scripts/local-gauntlet.sh` green; the `Aggregates` and `Projections` integration namespaces
      green against PostgreSQL. Verify: the run output, recorded here.
- [ ] 8.2 `openspec validate document-the-execution-models-operating-limits --strict` passes. Verify: the
      output.
