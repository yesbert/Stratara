## Context

`IProjectionReplayState.IsReplayActive` is a synchronous `bool` property on a published abstraction
(`Stratara.Abstractions`). Two implementations exist in `Stratara.Outbox.RabbitMQ`: the in-process one
answers from a leased field under a lock; the Redis-backed one, chosen as soon as the host registers an
`IConnectionMultiplexer`, answers with a blocking `StringGet` of
`stratara:projection:replay:active`. The marking is leased (`ProjectionReplayOptions.LeaseSeconds`,
default 300) and renewed by `SetProgress`, so it may lapse without a `Deactivate`. A replay request
already travels over a pub/sub channel (`stratara:projection:replay:request`) that every host
subscribes to.

Readers of the flag, all synchronous: `OrleansCommandDispatcher` (per dispatch, inside the send lane's
continuation, and per drain pass), `OrleansEventBundleDispatcher` (per commit, on the path the request
waits for), `StoreReaderGrain.Suspended` through `StoreReaderLoop` (at every catch-up's start and before
every batch, inside a grain turn), `OutboxDrainWork` (per pass on a silo without a dispatcher),
`ProjectionRebuilder` (per rebuild), and the bus dispatchers `CommandOutboxDispatcher` and
`EventBundleOutboxDispatcher` (per publish and per drain). The Orleans model's pausers already suspend
the readers during a rebuild and a full replay (`ReplayCheckpointResetTruncator`, `ProjectionRebuilder`);
the flag is the second belt there, not the first.

Evidence: the implementation at `main` 9c1c165; a consumer's nightly runs on a two-core agent, whose
every failed run carried `RedisTimeoutException`s on that GET with `WORKER: (Busy=30, Min=2)` and none in
the green runs; the Orleans integration tests, which never see it because the test machine's pool is
large.

## Goals / Non-Goals

**Goals:**
- No framework path blocks a thread on Redis to learn whether a replay is active, and none makes a
  round trip for it.
- A change of the marking is seen by every host within a bound the operator can read off the
  configuration.
- The abstraction, its call sites, the in-process state and a host's own state are untouched.

**Non-Goals:**
- Making `Activate`, `Deactivate`, `SetFailed`, `SetProgress`, `RequestReplay` and `GetProgress`
  asynchronous. They run on the replaying host once per replay or once per batch, or on a dashboard's
  poll, and `GetProgress` is the authoritative view by design.
- Reproducing the thread-pool starvation in a test. The pool's minimum is process-wide; a test that
  lowers it changes every other test in the process. What is proven is that the call is gone.
- An asynchronous `IsReplayActiveAsync` on the abstraction. See the first decision.

## Decisions

**A field, fed by a message and refreshed on a period — not an asynchronous read.** The finding offers
two shapes: keep the flag in memory, or make the read asynchronous. The asynchronous read fixes the
blocking and keeps the round trip: one Redis latency per command on the request's path, and several per
catch-up. The field removes both, and it needs no change to `IProjectionReplayState`, to the five Orleans
call sites, to the `Func<bool>` the store-reader loop takes, or to the bus dispatchers, which get the
same fix for free because they share the class. Evidence for the cost: the call sites listed in Context.

**The message is a wake-up, not the truth.** A new channel, `stratara:projection:replay:state`, is
published on `Activate`, `Deactivate` and `SetFailed`. The subscriber does not trust the payload; it
re-reads the key asynchronously. One code path — the refresh — serves the message and the timer, two
transitions in quick succession cannot reorder the field past the truth, and a payload format never has
to be versioned across a rolling upgrade. This is the shape the execution model already uses for commits:
the store is the truth, the nudge is a hint that may be lost. `SetProgress` publishes nothing; it renews
the lease, which the refresh sees.
- *Alternative:* Redis keyspace notifications on the key's expiry. Rejected: they need
  `notify-keyspace-events` on the server, a setting the host does not own, and are off by default.
- *Alternative:* the message carries the lease's expiry and the field lapses locally, as the in-process
  state does, with no refresh. Rejected: a lost `Deactivate` message would hold commands back for the
  whole lease.

**The refresh is the guarantee, the message is the latency.** The refresh runs on
`ProjectionReplayOptions.RefreshSeconds`, default 5, read from the same section as the lease and
validated with it: zero or below is refused, and so is a value at or above the lease, because a refresh
slower than the lease could miss a whole replay. A lost message or a subscription not yet established
costs at most one period of staleness; a message costs milliseconds. A refresh per host every five
seconds is one `GET` — negligible against the eight to ten per command the change removes.
- *Alternative:* a fixed fraction of the lease. Rejected: a tenth of the default lease is thirty
  seconds, too long for a lost message, and a cap would be a second hidden number. A named option is
  what the operate guide can point at and what a test with a two-second lease can set to one.
- *Alternative:* a short refresh and no channel. Rejected: a bus host that learns of a replay five
  seconds late publishes the commands the first replayed batches provoke, which *Publication is
  suppressed while a replay is active* exists to prevent. The channel makes the usual case immediate.

**Refreshes are serialised, the refresh is asynchronous end to end, and a transition of the host's
own outranks a read in flight.** The timer loop and the subscriber's handler both call one
`RefreshAsync`, which takes a `SemaphoreSlim(1, 1)` and uses `StringGetAsync`; an older read can
therefore not land after a newer one. A read that was in flight when `Activate`, `Deactivate` or
`SetFailed` ran on this instance may predate that transition, so each transition moves a generation
counter before it sets the field, and a refresh writes the field only where the generation is the one
it captured before its read; otherwise it discards what it read, and the next refresh reads again
(found by the review of #187; `ProjectionReplayStateTests` in the unit tests holds the read open with
a controlled connection). A subscription that cannot be established is a failure of its own — the
marking is still refreshed on the period — and is logged once per failing stretch (`104_017`,
`104_018`) apart from the refresh's pair, so a refresh that works does not report a recovery the
subscription never had. The loop is a
`PeriodicTimer` over the registered `TimeProvider`, started by the constructor and held in a field —
no `Task.Run`, no thread — and the class becomes `IAsyncDisposable`: disposal cancels the loop, awaits
it and unsubscribes. The container disposes the singleton it created through the factory. A service
collection without a host still gets a state that refreshes, because nothing depends on a hosted service
having started.

**The host's own transitions are seen at once.** `Activate`, `Deactivate` and `SetFailed` set the field
after their Redis write succeeded, before publishing. The replaying host therefore suppresses its own
publication from the first batch without waiting for its own message to come back; a failed write leaves
the field as it was, as the marking is.

**Before the first refresh, and when Redis cannot be reached, the answer is the last one — initially
`false`.** The constructor does not block, because the singleton is created on the first resolve, which
may be a request. A refresh that fails keeps the field, logs `104_015` once per failing stretch and
`104_016` when the next refresh succeeds; a subscription that could not be established is tried again on
each tick. This changes what a host sees when Redis is down: before, every dispatch and every catch-up
threw the connection's exception; now they proceed on the last answer. That is the better failure — a
replay is the rare case, and the flag was never what kept a replay correct on the Orleans model, the
pausers and the readers' second pass are — and the CHANGELOG says so.

**Tests.** The Redis-backed state has integration tests only
(`tests/Stratara.Outbox.RabbitMQ.IntegrationTests/Projections/ProjectionReplayStateTests.cs`, on a
Testcontainers Redis), and that is where the new behaviour is pinned, each on a real connection:
- *no round trip:* with the refresh set long, read `IsReplayActive` a thousand times and compare the
  server's `INFO commandstats` count of `get` before and after — it must not move. The collection runs
  its tests one at a time, so the server-wide counter is stable; the alternative, a counting decorator
  over `IConnectionMultiplexer`, is more code for the same fact.
- *propagation:* two states over one connection; `Activate` on one is seen on the other well within the
  refresh period (the message), `Deactivate` and `SetFailed` likewise.
- *lapse and lost message:* delete the key behind both states' backs; both answer inactive within one
  refresh period, with the refresh at one second.
- *own transition:* `Activate` on a state is seen on that state before any message could arrive.
- *options:* the binding test gets the refresh's three refusals, and the documentation test's section
  case covers `RefreshSeconds`.
The Orleans integration tests keep passing unchanged: the one that waits for `!replay.IsReplayActive`
waits on the host that deactivates, which sees it at once.

## Risks / Trade-offs

- [Risk] A host on a release before this one starts a replay: it publishes no state message, so a
  host on this release sees it at the refresh period, not at once. → Bounded by the period; said in
  the CHANGELOG under rolling upgrades. The marking itself, the key and the lease are unchanged.
- [Risk] A bus host that misses the message publishes what the first replayed batches provoke for up to
  one refresh period. → Was impossible before, since every read went to Redis. The channel makes it
  the exception, the five-second default bounds it, and the operator may shorten it. On the Orleans
  model the readers are paused by the pausers regardless.
- [Risk] A refresh that keeps failing hides a replay from the host for as long as Redis is away.
  → Logged once at warning with the exception and once at information on recovery; a host whose Redis
  is away has larger problems — its grain directory lives there.
- [Trade-off] One subscription and one `GET` per host per period, forever, against none when no replay
  runs. → The removed cost is eight to ten blocking `GET`s per command.
