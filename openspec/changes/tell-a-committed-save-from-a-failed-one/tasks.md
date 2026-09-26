## 1. Reproduce first

- [x] 1.1 `tests/Stratara.Infrastructure.Tests/EventSourcing/EventSourceSaveOutcomeTests.cs`, against the
  SQLite test store:
  - a save with nothing staged publishes no bundle (fails on `main`, which publishes an empty one);
  - a save whose bundle handover throws after the commit fails with
    `CommittedEventsNotPublishedException` naming the stream, and the events are in the store (fails on
    `main`, which throws the handover's exception).
- [x] 1.2 `tests/Stratara.Shared.Tests/Resilience/ResilienceFactoryTests.cs`: the command and bundle
  dispatcher pipelines run `CommittedEventsNotPublishedException` once.

## 2. The fix

- [x] 2.1 `CommittedEventsNotPublishedException` in `src/Stratara.Abstractions/Abstractions/EventSourcing/`,
  fully documented, with the stream ids and the event count.
- [x] 2.2 `EventSource`: store and publish no bundle when nothing is staged (the session is still
  checked); wrap a failure of the handover after the commit, cancellation excepted.
- [x] 2.3 `ResilienceFactory`: the dispatcher retry does not handle the new exception.
- [x] 2.4 `IEventSource.SaveChangesAsync` documents both.

## 2b. Nothing runs committed work again (found in review)

- [x] 2b.1 `EventSource` wraps a cancelled handover after the commit too
  (`EventSourceSaveOutcomeTests.A_handover_cancelled_after_the_commit_still_says_the_events_are_committed`).
- [x] 2b.2 `RabbitMqBus` and `AzureServiceBusBus` acknowledge the message and log `108_113`
  (`RabbitMqDeadLetterTests.HandlerCommittedButCouldNotPublish_…`,
  `ServiceBusTests.SubscribeAsync_HandlerCommittedButCouldNotPublish_MessageIsCompleted`; the RabbitMQ one
  runs the handler three times on `main`).
- [x] 2b.3 `CommandExecution.RunIntentAsync` completes the intent and logs `117_127`
  (`IntentCommittedNotPublishedTests`, which fails without the change).
- [x] 2b.4 `ResilienceNames.MessageBus` and `ProjectionReplayBatch` exclude it too; the dispatcher
  pipelines still retry an ordinary failure and not cancellation (`ResilienceFactoryTests`).
- [x] 2b.5 `RabbitMqDeadLetterTests.MeterCapture` reads the metric names before its listener starts, so
  the metrics' first initialisation does not call back into a half-built listener.

- [x] 2b.6 A framework exception crosses silos with its type: the execution model's registrations add
  `Stratara` to Orleans' `ExceptionSerializationOptions.SupportedNamespacePrefixes`, and the properties of
  `ConcurrencyException` and `CommittedEventsNotPublishedException` read empty rather than null after a
  crossing (`FrameworkExceptionSerializationTests`; without the registration the round trip fails).

- [x] 2b.7 Round 3 of the review: the transports settle with `CancellationToken.None`; a durable host does
  not fail a save whose handover fails after the commit (`EventSourceSaveOutcomeTests`); a store reader
  counts such an entry as applied (`StoreReaderLoopTests`) and a durable timer as fired
  (`CommittedTimerTests`, integration); the RabbitMQ test proves the message was acknowledged by closing
  the consumer and finding the queue empty, the Service Bus one by peeking the subscription; the
  existing requirements that promised a retry carry the carve-out (MODIFIED deltas); log ids
  `117_127`–`117_129` in the schema page; "republish" is gone from the docs, which the framework does
  not offer.

- [x] 2b.8 Round 4 of the review: every exception type crosses silos, so a provider's inner failure does not
  stop a framework exception from crossing (`FrameworkExceptionSerializationTests` with a real Npgsql chain), a
  host's own filter kept; the prefix is `Stratara.`; the framework exceptions' properties read empty after a
  crossing; a timer's unregister after committed work ignores the stopping token; a stopping RabbitMQ
  subscription cancels its consumer and waits for running handlers to settle before closing the channel, and
  every acknowledgement of a handled message uses `CancellationToken.None` (`RabbitMqDeadLetterTests.SubscriptionStopsDuringTheHandler_…`,
  which found the message back in the queue before); the durable-host test checks the events committed.

- [x] 2b.9 Round 5 of the review: a stopping RabbitMQ subscription refuses deliveries it had buffered, from the
  moment its token is cancelled (`SubscriptionStops_BufferedDeliveriesGoBackToTheQueueUnhandled`); the
  cleanup runs without `Task.Run`; a stopping Service Bus subscription stops its processor, which waits for
  its running handlers, and the bus awaits that on disposal
  (`SubscribeAsync_SubscriptionStopsDuringTheHandler_TheMessageIsCompletedAndNoMoreAreTaken`); the
  documentation no longer promises that a host's exception filter restricts anything;
  `ErasureIncompleteException.Plane` says what it reads after a crossing.

- [x] 2b.10 Round 6 of the review: the host waits for its stopping subscriptions when it stops, not when its
  container is disposed — a hosted lifecycle service per transport awaits them in `StoppedAsync`
  (`HostStopsDuringTheHandler_TheHostWaitsForIt…` on both transports); a stopping Service Bus subscription
  closes its processor within twenty seconds instead of waiting without bound; a RabbitMQ subscription holds
  at most `Messaging:PrefetchCount` messages (default 16), because what it holds goes back counted
  (`HandlerRuns_TheSubscriptionHoldsNoMoreThanItsPrefetchBound`, and the buffered test checks the redelivered
  flag and the count); every settlement uses `CancellationToken.None`; a channel that fails to close no
  longer keeps its connection open, and a subscription that fails to open closes its connection; a handler
  that never returns no longer keeps a RabbitMQ channel's close — and the bus's disposal — waiting forever
  (`HandlerNeverReturns_StoppingTheSubscriptionStillFinishes` on both transports; found by running the host
  test without the drain); a
  validation failure's message no longer lists its failures — a failure's message may quote the input —
  and its failures cross silos through a surrogate instead, without the attempted value
  (`FrameworkExceptionSerializationTests.A_validation_failure_keeps_which_field_failed_…`).

- [x] 2b.11 Round 7 of the review: a handler that stops on its subscription's token has its message put back —
  requeued or abandoned — instead of counted as a failure, which on the last allowed delivery dead-lettered it
  (`SubscriptionStopsAndTheHandlerGivesUp_…` on both transports); a Service Bus message taken after the
  subscription started stopping is abandoned unhandled; while the host stops, a stopping subscription waits for
  its handlers as long as the host's shutdown timeout allows instead of twenty seconds, handed over in the
  drain's `StoppingAsync` (the host tests now hold the handler for twenty-two seconds under a sixty-second
  timeout), and cancelling the RabbitMQ consumer counts against the same deadline; a handler that never returns
  holds neither the host's stop nor its disposal up (`HostStopsWhileAHandlerNeverReturns_…` on both
  transports, which found that the drain's timeout branch could dispose its registration before the
  registration's callback ran, so the drain now says so itself); a Service Bus close abandoned after the
  deadline has its fault logged; the drains take the concrete bus, so a decorated `IMessageBus` does not hide
  it (superseded in round 8); the documentation says that a validation failure from an upgraded silo cannot be read by an older one during a
  rolling upgrade, and when RabbitMQ puts a stuck handler's message back.

- [x] 2b.12 Round 8 of the review: a Service Bus subscription whose wait had already run out — a host with no
  shutdown time left — cancelled its processor's close before it began, so once its handler returned the
  processor kept receiving and handed every new message back; the close is no longer cancellable, only the wait
  for it (`HostStopsWithNoTimeLeft_TheProcessorStillStopsTakingMessages`, red with the old close); a host's stop
  no longer outlives it — a subscription stopped after the host has stopped waits twenty seconds again; the
  application's stopping token starts the host's wait as well; the hosted service is a tracker the bus reports
  its stops to, so it no longer builds the bus, `IMessageBus` is registered by type again and the bus is
  disposed once (`A_host_that_replaced_the_message_bus_starts_without_building_the_transport`); the documents
  say that the broker counts a put-back delivery, that an infinite shutdown timeout waits as long as the
  handler, and that a rolling upgrade breaks validation failures in both directions.

- [x] 2b.13 Round 9 of the review: a subscription tied to the application's stopping token stopped in one of that
  token's callbacks before the one that marked the host as stopping — callbacks run newest first — and waited
  twenty seconds instead of the host's timeout; the tracker now reads whether the application is stopping when a
  deadline is taken (`SubscriptionStopsTests.A_stop_tied_to_the_applications_stopping_token_…`,
  `ApplicationStopsWhileAHandlerRuns_ASubscriptionTiedToItWaitsUnderTheHostsTimeout`); a host that stopped in time
  gives a stop still under its deadline the standalone bound instead of cutting it short
  (`A_host_that_stopped_in_time_does_not_cut_short_…`); a host disposed without being stopped after its
  application was told to stop no longer leaves the bus's disposal waiting without bound; a Service Bus close that
  faults after its wait ran out is observed and logged (`AzureServiceBusBus`, the stop's catch; not reproducible
  against the emulator); the stop with no time left is verified on RabbitMQ too
  (`HostStopsWithNoTimeLeft_TheSubscriptionStillStopsTakingMessages`), and the spec names both new scenarios.

- [x] 2b.14 Round 10 of the review: the host's stop in the trackers is guarded by one lock and ends — once the host
  has stopped, or when the bus is disposed — for good, so a deadline taken late can no longer install a host stop that
  never runs out, and a stop that begins after the bus's disposal keeps the standalone bound; the trackers take a
  `TimeProvider`, and `SubscriptionStopsTests` now pins both bounds with a fake clock
  (`Once_the_host_has_stopped_…_runs_out_with_the_standalone_bound`,
  `A_host_disposed_without_being_stopped_leaves_no_stop_without_a_bound`,
  `A_bus_disposed_before_its_application_was_told_to_stop_leaves_no_stop_without_a_bound`); the RabbitMQ host tests
  (`HostStopsDuringTheHandler_TheHostWaitsForItAndTheMessageIsAcknowledged`,
  `ApplicationStopsWhileAHandlerRuns_ASubscriptionTiedToItWaitsUnderTheHostsTimeout`) check at twenty-two seconds
  that the message is still the handler's, which the old twenty-second wait fails.

- [x] 2b.15 Round 11 of the review: a stop that arrived while the store committed reported a committed save as
  cancelled, and a transport then put the message back and ran it again — first fixed in `EventSource`, then moved
  to the store in round 12 (2b.16);
  the trackers keep real time whatever `TimeProvider` a host registers — a frozen test clock had frozen the bus's
  disposal — through a constructor the container never chooses
  (`TransportSelectionTests.The_stopping_subscriptions_keep_real_time_whatever_clock_the_host_registers`); the
  documents say the host waits for the subscriptions its hosted services stopped.

- [x] 2b.16 Round 12 of the review: making the whole save uncancellable (round 11) let a stop mid-save commit and then
  lose the bundle to a cancelled handover, and the pattern was not the event source's alone — every commit under a
  caller's token had it. Reverted in `EventSource`; instead the new public `CommitCompletionInterceptor`
  (`src/Stratara.EventSourcing.EntityFrameworkCore/EntityFrameworkCore/`) commits with no token and suppresses the
  original call, and gives single-statement saves a transaction; the framework's Npgsql registration and both test
  hosts add it (`CommitCompletionInterceptorTests`, five facts on SQLite, one showing SQLite abandons the commit
  without it; `EventSourceSaveOutcomeTests.A_save_cancelled_while_the_store_commits_is_not_reported_as_cancelled`,
  red without the interceptor; `A_save_cancelled_before_it_commits_writes_no_events`); `PartitionCounterInterceptor`
  commits with no token; `SagaProcessGrain` reads its state and cancels its timers with no token once its step is
  saved; `EventBundleOutboxDispatcher` records a bundle whose publication the caller's cancellation cut short, with
  no warning for the cancellation
  (`EventBundleOutboxDispatcherTests.EnqueueEventBundleAsync_PublishCancelledByTheCaller_StillRecordsTheBundle`);
  documented in `IEventSource.SaveChangesAsync`, the migration guide and the CHANGELOG.

- [x] 2b.17 Round 13 of the review: the guarantee no longer depends on a context carrying the interceptor — the
  unit of work (`UnitOfWork<TDbContext>`'s transaction) writes under the caller's token and commits without it on
  any relational context, inside the context's execution strategy (superseded in round 14, 2b.18);
  `PartitionCounterInterceptor` releases its transaction on a cancelled save, which EF reports to
  `SaveChangesCanceledAsync`, not `SaveChangesFailedAsync`
  (`PartitionCounterCancelledSaveTests.A_cancelled_append_leaves_no_transaction_open_for_the_next_save`, red
  without the override); `SagaProcessGrain` logs and swallows a failure to reread its state or cancel its timers
  after its step committed (`LogEvents.Orleans.SagaStepAftermathFailed`, 117_130; not reproducible without fault
  injection in the grain); the dispatcher test checks that a cancelled publish logs no warning; the interceptor's
  documentation says it must come last among transaction interceptors and what bounds a commit; the spec scopes the
  guarantee to the stores the framework provides and the cancelled-handover scenario to a handover the host provides.

- [x] 2b.18 Round 14 of the review: the unit of work's own transaction (2b.17) threw under an ambient transaction,
  bypassed a context's `SaveChangesAsync` override, raised `SavedChanges` before the commit and cost a savepoint
  round trip — reverted. The unit of work instead reads whether the context carries `CommitCompletionInterceptor`
  and, where it does not, saves without the caller's token, so that save runs to its end whole
  (`CommitCompletionInterceptorTests.The_unit_of_work_saves_whole_on_a_context_without_the_interceptor`,
  `…honours_a_cancellation_during_the_writes_on_a_context_with_the_interceptor`); `PartitionCounterInterceptor`
  released its transaction on a concurrency conflict too (reverted in round 15, 2b.19); `SagaProcessGrain` swallows a failure to cancel its timers only when its step
  committed; the interceptor's documentation names what a retrying execution strategy does to a commit whose
  acknowledgement was lost.

- [x] 2b.19 Round 15 of the review: releasing the partition counter's transaction on a concurrency conflict rolled it
  back while Npgsql's reader was still open, which turned a conflict into a plain store failure — reverted; the
  framework's appends are inserts and cannot raise that conflict. `CommandIntentStore` runs every statement that
  changes a record — the record, the claims, the renewals, the returned attempt, the kept mark — to its end, so a
  committed record reported as cancelled no longer has the caller dispatch the command again, nor a claim spend an
  attempt (`CommandIntentStore`, `CancellationToken.None` at each such statement; the window is a single statement's
  acknowledgement and not reproducible on SQLite). `UnitOfWork<TDbContext>` treats a context whose
  `AutoTransactionBehavior` is `Never` as not covered, and documents the fallback and what bounds it; the guide
  adds the interceptor to the read context as well; the spec and the CHANGELOG say a cancelled save leaves none of
  its events behind — a snapshot written before them is change `write-a-snapshot-only-of-committed-events`' to
  move, and `design.md` says so.

- [x] 2b.20 Round 16 of the review: with commits running to their end, a handler that finished after its silo's
  deactivation budget ran out returned normally into code that still used the cancelled token. `TimerOwnerGrain`
  now takes its gate with no token once the handler has returned, so the reminder is unregistered rather than fired
  again (the path `CommittedTimerTests` covers for a handover failure); `StoreReaderLoop` records the checkpoint of a
  full batch with no token, like the cut batch already did
  (`StoreReaderLoopTests.A_batch_that_finishes_as_the_reader_stops_still_records_its_checkpoint`, red with the
  token). `CommandIntentStore.RenewAsync` keeps the caller's token again — a renewal reported cancelled does no harm,
  and the lease's disposal waits for it; the CHANGELOG no longer claims the interceptor covers checkpoint updates,
  and the interceptor's documentation says what `Never` leaves uncovered.

- [x] 2b.21 Round 17 of the review: Orleans completes a grain call whose token is cancelled at once, whatever the callee
  does, so a saga step called by a stopping timer tick (`SagaProcessTimerHost.OnDueAsync`) or saga reader
  (`SagaReaderRun.ApplyAsync`) that committed looked cancelled and ran again. The execution model's registrations now
  set `MessagingOptions.WaitForCancellationAcknowledgement` for silo and client (`FrameworkCallCancellation`), so the
  cancellation still reaches the callee and the caller gets its answer
  (`FrameworkCallCancellationTests.The_execution_models_registrations_wait_for_the_callees_answer`;
  `CancelledCallOutcomeTests` on a localhost silo — a call cancelled while its callee commits reports the commit, and
  without the registration a cancellation); documented in the operate guide and the CHANGELOG; `TimerOwnerGrain`'s and
  `StoreReaderLoop`'s summaries say what round 16 changed.

- [x] 2b.22 Round 18 of the review: with calls waiting for their callee, a completed saga step's post-commit call to a
  timer owner deactivating with the silo waited for its response timeout instead of ending with the step's budget;
  `SagaProcessGrain` passes the step's token to that call again, swallowing its cancellation (the owner check drops
  the timers of a completed process). The documentation no longer claims forwarded commands or timer handlers are
  affected — their calls carry no token — and says a callee that does not answer ends the call at the response
  timeout; the three registrations' XML remarks, the cheatsheet and the `orleans-execution` delta (a requirement
  paragraph and the scenario `CancelledCallOutcomeTests` verifies) state the host-wide change.

## 3. Documentation

- [x] 3.1 `docs/guides/write-a-command-handler.md` and `docs/guides/use-resilience-policies.md`.
- [x] 3.2 `CHANGELOG.md` → *Unreleased*.

## 4. Verify

- [x] 4.1 `openspec validate tell-a-committed-save-from-a-failed-one --strict`.
- [x] 4.2 Local gauntlet green.
