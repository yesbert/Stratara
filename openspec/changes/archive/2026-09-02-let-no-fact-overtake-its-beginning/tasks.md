## 1. See it fail first

- [x] 1.1 Write the worker test that decides the serialisation, in a new
      `tests/Stratara.Projections.Tests/Services/ProjectionWorkerTests.cs`: a fake bus that hands two
      bundles for the same stream to two consumers at once, and a recording projection that notes
      when each apply begins and ends. Verify: today the intervals overlap, and the test asserts they
      do not.
- [x] 1.2 Write the retry test alongside it: a projection that throws the missing-prerequisite
      exception on the first attempt and succeeds on the second. Verify: it fails to compile today
      because the exception does not exist, which is the failure that names the missing member.
- [x] 1.3 Write the release-between-attempts test: bundle B reports a missing prerequisite for stream
      S; while it waits, bundle A for stream S must be able to acquire the lock and apply. Verify:
      A completes before B's second attempt, and B succeeds on that attempt.
- [x] 1.4 Mirror 1.1–1.3 in a new `tests/Stratara.Sagas.Tests/Services/SagaWorkerTests.cs` against
      `SagaWorker` and a recording saga. Verify: same three assertions.

## 2. The building blocks

- [x] 2.1 Move `BucketLockPool` from `src/Stratara.Mediator/` to
      `src/Stratara.Abstractions/Abstractions/Partitioning/`, public, with a `BucketCount` constant
      and XML documentation; delete the mediator's copy and its mirrored 4096, and make
      `BucketConstants.TotalBucketCount` in `src/Stratara.Shared/Partitioning/` refer to
      `BucketLockPool.BucketCount`. Verify: a new `tests/Stratara.Shared.Tests/BucketLockPoolTests.cs`
      beside `BucketCalculatorTests.cs` covers acquire-blocks-same-bucket,
      acquire-does-not-block-other-bucket and release-on-dispose;
      `tests/Stratara.Outbox.RabbitMQ.Tests/Mediator/MediatorCommandWorkerTests.cs` stays green
      unchanged; `grep -rn 4096 src` finds the number once.
- [x] 2.2 Add `PrecedingFactMissingException(Guid streamId, string eventTypeName, Exception? innerException = null)`
      to `src/Stratara.Abstractions/Abstractions/EventSourcing/`, shaped like `ConcurrencyException`
      next to it, with `StreamId` and `EventTypeName` properties and XML documentation that says what
      the framework does with it and how long it waits. Verify: task 1.2 now compiles and fails on
      behaviour instead.
- [x] 2.3 Add `ResilienceNames.PrecedingFact = "PrecedingFactPipeline"`, build it in
      `ResilienceFactory` (five attempts, 100 ms, exponential, jitter, handling only the new
      exception) and register it in `ResilienceServiceCollectionExtensions.AddStrataraResilience`.
      Verify: `tests/Stratara.Shared.Tests/Resilience/ResilienceFactoryTests.cs` shows the new policy
      retrying the new exception and nothing else, and the registration test in
      `tests/Stratara.Infrastructure.Tests/Resilience/ResiliencePipelineBehaviorTests.cs` — the one
      place `AddStrataraResilience` is exercised — resolves all five names and still registers
      idempotently.

## 3. The workers

- [x] 3.1 Add `int? DegreeOfParallelism` to `ProjectionOptions` and `SagaOptions`, documented with
      the fallback. Verify: `ProjectionServiceCollectionExtensionsTests` and the saga counterpart bind
      it from the `Projections` / `Sagas` sections, and a value of zero or less resolves to
      `Environment.ProcessorCount`.
- [x] 3.2 `ProjectionWorker`: read the option in `ExecuteAsync`; in `HandleEventBundleAsync` compute
      the distinct bucket ids of the bundle's streams, sort them, and run acquire-apply-release inside
      the `PrecedingFact` policy so that each attempt holds the locks and each wait holds none.
      Verify: tasks 1.1–1.3 pass; an empty bundle takes no lock (existing empty-bundle test still
      green).
- [x] 3.3 `SagaWorker`: the same change. Verify: task 1.4 passes.
- [x] 3.4 Log each retry at warning level through a source-generated `[LoggerMessage]` in the
      projection and saga logger extensions, with the stream id, the event type and the attempt
      number, using the next free ids in `LogEvents.Projection` (104_0xx) and `LogEvents.Saga`
      (110_0xx). Verify: the logger-extension tests assert the event id and the template
      placeholders; the retry test in 1.2 sees one warning.
- [x] 3.5 Update the XML `<summary>` of both workers, which currently promise "one subscription per
      `Environment.ProcessorCount`". Verify: the summary names the option and the per-aggregate
      serialisation, and `dotnet build -c Release` is warning-free.

## 4. Say what a handler may rely on

- [x] 4.1 `docs/guides/write-a-projection.md`, under *Idempotency is your job*: add that bundles about
      one aggregate are applied one at a time within a process, that order across processes is not
      promised, and show the two-line pattern — look up, throw `PrecedingFactMissingException` if
      absent — with what the framework does next. Verify: the guide names the exception, the retry
      bound, and the option, and says "within a process".
- [x] 4.2 `docs/guides/write-a-saga.md`, under *Idempotency*: the same, for a saga that reads before it
      dispatches. Verify: same three items.
- [x] 4.3 Both outbox guides (`outbox-setup-rabbitmq.md`, `outbox-setup-azureservicebus.md`): document
      `Projections:DegreeOfParallelism` and `Sagas:DegreeOfParallelism` next to the worker registration,
      with the fallback and the sentence that a host needing strict order sets it to one. Verify: the
      configuration snippet shows the key and the guide says what a non-positive value does.
- [x] 4.4 Correct the `<remarks>` on `EventBundleOutboxDispatcher`, which say at-least-once and stop:
      add that order across consumers is not guaranteed and point at the worker guarantees. Verify:
      the generated XML for `Stratara.Outbox.RabbitMQ` carries the sentence.
- [x] 4.5 Update `llms.txt` so an assistant answering "why did my projection see the update before the
      create" reaches the serialisation, the exception and the option. Verify: `llms.txt` names all
      three, and `llms-full.txt` is regenerated rather than hand-edited.

## 5. Close it out

- [x] 5.1 Write the changelog entry under `## [Unreleased]` in *Added*: the per-aggregate
      serialisation in both workers, the exception and what throwing it buys, the two options with
      their fallback, and the new named policy. Verify: it says the change is silent for a consumer
      that changes nothing, and that the guarantee is per process.
- [x] 5.2 Run the gate. Verify: `./scripts/local-gauntlet.sh` green, including the new tests in
      `Stratara.Projections.Tests`, `Stratara.Sagas.Tests`, `Stratara.Shared.Tests` and the resilience
      tests.
