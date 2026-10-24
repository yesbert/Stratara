## Context

See proposal.md, *Why*. These are the parts of 4.4.7 the approach depends on.

- **Store readers.** The base grain of projection and saga readers reads on three triggers. A nudge is
  one-way and interleaving, and requests a catch-up without waiting for it. The poll is a grain timer with
  `Interleave = false` and `KeepAlive = true`, and its callback returns the running loop. The keep-alive
  reminder's `ReceiveReminder` also returns the running loop. `StoreReaderLoop.RequestCatchUp` hands back the
  loop in flight, or starts one. The loop, not the grain's turn, keeps one catch-up per partition (its
  remarks: "the serialisation the grain's turn no longer provides once a nudge may interleave"). A trigger
  during a loop sets `_dirty`, so the loop reads once more.
- **Consequence.** A poll tick holds the activation for the whole catch-up. Every call that does not
  interleave queues behind it, and that includes the reminder. That is F-028's
  `IsExecuting: False, IsWaiting: True`. Once the reminder runs, it waits for the loop as well, so it would
  time out on its own.
- **Singleton work.** The grain runs its work on a grain timer with `Interleave = false`, on purpose: a run
  never overlaps with itself or with a call. Its `ReceiveReminder` only calls `StartTimer()`, which is
  synchronous and idempotent. It still queues behind a run that is in progress.
- **Start calls.** At `ServiceLifecycleStage.Active`, `StoreReaderGrainStarter` and `SingletonWorkStarter`
  await `EnsureRunningAsync()` on every reader and every work. That method does not interleave either, so it
  queues behind the same long turns.
- **Durable timers.** `TimerOwnerGrain` is `[Reentrant]`. Its `ReceiveReminder` awaits the handler, and that
  open call is what keeps the owner activated while the handler runs (see *Non-Goals*).

## Goals / Non-Goals

**Goals:**

- Answer every keep-alive tick of a store reader or a singleton work within the response timeout.
- Keep the guarantees exactly as they are: one catch-up per partition, one run per work, pauses, retirement,
  waiting for a projection, and failover.
- No change to a grain interface's shape or its aliases, so a rolling deployment of 4.4.7 and this release
  works.

**Non-Goals:**

- **Durable timers.** If `ReceiveReminder` returned at once, the owner would have no request in flight
  while its handler runs. The activation would then be open to idle collection mid-handler. The fix would
  need the self-call pattern the heavy-work pool uses (`return-a-long-hand-over-once-it-is-accepted`). It
  would also re-open the "fires once however long its handler runs" scenarios. Too much for a patch that
  closes F-028. This change only documents it.
- **`CatchUpAsync()`, the explicit call that waits for a catch-up.** It keeps waiting for the loop. Only
  tests and old silos call it.

## Decisions

### D1 — Keep-alive tick and poll request a catch-up and return at once

`ReceiveReminder` and the poll timer callback call `RequestPoll()`. It lets lapsed pauses lapse, starts a
catch-up where none is running without awaiting it (the shape `NudgeAsync` already has), and returns at once.

- **A running loop is left alone (review finding, 2026-10-24).** The first cut requested a catch-up on every
  tick, so each poll set `_dirty` on a running loop. A loop stalled on an entry whose retry outlasts the poll
  interval was then re-dirtied every round, retried the entry back to back and never ended. `Running` never
  cleared, and every deactivation had to cancel it. Before this change a poll could re-dirty a loop at most once,
  because the tick then waited for it. Now a poll does not touch a running loop. It reads until the store has
  nothing newer, and a commit's nudge still makes it read once more. A fact whose nudge was lost after the loop's
  last read is read by the first poll after the loop ends, one poll interval at most. That is the promise of
  *a wake-up that is lost costs latency and never a fact*.

- **Why it is safe.** One loop per partition is kept by the loop, not by the turn, so a trigger that does not
  wait changes nothing about ordering or checkpoints. A failed loop is logged and counted by the loop itself,
  whoever started it, and the next trigger reads again. That is the existing comment on `NudgeAsync`.
  `OnDeactivateAsync` already waits for a running loop, whatever started it. A loop outside any turn is
  already what a nudge produces, so this adds no new state.
- **Activation lifetime.** The poll timer keeps `KeepAlive = true` and keeps ticking every poll interval
  (5 s by default) during a catch-up. So a reading grain stays activated, as it does today after a nudge.
- **The poll timer keeps `Interleave = false`.** Its tick now lasts only as long as the synchronous lapse
  check, so it no longer holds the activation. Making it interleave would add nothing.
- **Alternative rejected:** keep awaiting the loop but let the reminder interleave. The reminder call would
  then stay open for the whole catch-up and time out on its own, which is F-028's "IsExecuting: True"
  variant.
- **Evidence:** `StoreReaderLoop.RequestCatchUp` and its remarks; `NudgeAsync`; the existing nudge tests in
  `ProjectionGrainTests`.

### D2 — The keep-alive tick and the start call interleave

- **Store readers.** `IRemindable.ReceiveReminder` belongs to Orleans, so no attribute can go on that
  interface method. Instead the reader grains get `[MayInterleave(nameof(MayInterleave))]`. The predicate
  returns `true` for an `IInvokable` whose interface is `IRemindable`. `EnsureRunningAsync` gets
  `[AlwaysInterleave]` on `IProjectionGrain` and `ISagaReaderGrain`. The retired `ISagaGrain` interface stays as
  it is, because its contract is what silos before 4.2.0 compiled against. Its grain reads nothing and gets the
  predicate only so that every store reader carries it.
- **Singleton work.** It gets the same predicate, and `[AlwaysInterleave]` on
  `ISingletonWorkGrain.EnsureRunningAsync`.
- **Why it is safe for singleton work.** The run timer stays non-interleaving, so runs still never overlap.
  `StartTimer()` is synchronous and returns when a timer exists. `RegisterOrUpdateReminder` touches only
  the reminder table, and a run never does.
- **Why it is safe for readers.** The interleaving parts are `Pausers.Lapse`, `Loop.Invalidate` and
  `RequestCatchUp`. They run on the activation's scheduler and are already designed to interleave with a
  running loop, because pauses and nudges do it.
- **Wire compatibility.** `[AlwaysInterleave]` travels with the message: the caller's generated proxy sets
  the flag, and the receiver honours it. A first draft said the receiver's metadata decides, which is wrong. The
  `[MayInterleave]` predicate is the receiver's. Aliases stay, so a mixed cluster works:
  - **A 4.4.7 silo starts and calls a reader or work on a new silo.** It sends no flag, so its start call still
    queues behind a long run, as today, until it is upgraded too.
  - **A new silo calls a grain on a 4.4.7 silo.** The flag makes the old grain interleave the call. That is
    safe there too, because its `EnsureRunningAsync` only registers the reminder and starts a timer that already
    exists.
  - **The reminder tick** is decided by each receiver's own predicate, so a new silo's grains answer at once
    whoever delivers the tick.
- **Where the predicate goes.** Orleans may or may not read `MayInterleave` from a base class. The attribute
  goes on each concrete grain (`ProjectionGrain`, `SagaReaderGrain`, `SagaGrain`, `SingletonWorkGrain`), calling
  one shared static predicate (`KeepAliveInterleaving.IsReminderTick`). It matches every reminder, since it cannot
  see the name, so these grains keep no reminder but their keep-alive. The attribute's documentation requires a public
  static method "declared on the same class", so each grain declares its own.
- **Alternative rejected:** making the grains `[Reentrant]`. Every call, and the singleton's run timer, would
  interleave, and that gives up the turn-based guarantees the singleton relies on.
- **Evidence:** Orleans 10.4.0 `MayInterleaveAttribute` documentation (signature
  `public static bool MayInterleave(IInvokable req)`); the integration tests of D3.

### D3 — Verify against the runtime's own log

The tests follow `LongHandOverTests`: a shortened `SiloMessagingOptions.ResponseTimeout` (2 s), a shortened
keep-alive period through `ExecutionModelTestHostOptions`, and the PostgreSQL store. The silo's log is
captured and checked for `Could not deliver reminder tick` and `Response did not arrive on time`.

- **Readers.** A projection, and a saga, whose apply is slowed so that the catch-up lasts several keep-alive
  periods.
- **Singleton work.** A work whose run lasts several keep-alive periods.
- **Start calls.** A second silo started during that singleton run.
- **Red first.** Each test is run against the unchanged code first, so that it is known to fail for the
  reason F-028 names.
- **Red run (2026-10-24, unchanged 4.4.7 code).** All four tests failed for the reason F-028 names:
  - Projection, saga reader and singleton work: the silo log held `Could not deliver reminder tick` and
    `Response did not arrive on time` for `IRemindable.ReceiveReminder`.
  - Second silo started during a long singleton run: its start failed with
    `TimeoutException: Response did not arrive on time … ISingletonWorkGrain.EnsureRunningAsync()`. So the start
    call is not only noisy. It fails the joining silo.
- **Green run.** With D1 and D2 in place, all four pass.

## Risks / Trade-offs

- [A tick that returns at once hides a loop that never ends] → nothing changes here: a stuck loop is already
  invisible to the tick. Stalls are reported by `orleans.reader.stalled` and `117_101`/`117_103`, not by the
  reminder.
- [`MayInterleave` predicate matches more than the reminder] → it matches on the interface type
  `IRemindable` only. A unit test calls the predicate with an invokable of each grain interface method and
  with the reminder's.
- [Interleaved `EnsureRunningAsync` on a reader racing with activation] → `OnActivateAsync` completes before
  any message, interleaving or not, is delivered. `EnsureRunningAsync` only registers or updates the
  reminder, so two of them are idempotent. Two that overlap now each upsert the same reminder with the same
  period. The table's upsert has no ordering to lose, so the last writer writes what the first did.
- [A pause's lease lapses while `PauseAsync` waits for a running loop, now that the tick interleaves] → nothing
  new. `PauseAsync` interleaves, so a poll could already run `Lapse` during that wait. `PauseAsync` holds the
  pause again once the loop has ended ("a pause that lapsed during that wait holds again"). Leases are minutes,
  renewed every period.
- [A new reader grain forgets the predicate] → `KeepAliveInterleavingTests` finds every concrete store reader
  by reflection instead of listing them. The retired `SagaGrain` carries the predicate as well, so the rule has
  no exception.
- [A tick during a deactivation] → a reader or work that is being deactivated waits for its running catch-up or
  run, and the runtime holds messages for it meanwhile, so a tick in that window can still time out. The spec
  allows it, and the operating guide says so.
- [Test timing flakiness with a 2 s timeout] → slow the apply by a fixed amount per entry, size the backlog
  for at least three keep-alive periods, and assert on the absence of a log line after the work ended, not on
  a count during it.

## Migration Plan

No migration. Ship as a patch. A rolling deployment is safe, see D2. Rollback means redeploying 4.4.7: the
reminders and checkpoints are unchanged, and F-028's log lines return.
