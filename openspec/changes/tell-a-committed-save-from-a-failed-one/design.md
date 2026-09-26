## Context

`EventSource.SaveChangesAsync` prepares the bundle, commits entries, snapshots and (durable mode) the
bundle row in one transaction, then calls `IEventBundleOutboxDispatcher.EnqueueEventBundleAsync`. In
non-durable mode the outbox dispatcher tries the bus and falls back to an
outbox row in a separate transaction; a failure of that write propagates. The transports redeliver
any handler failure except a concurrency conflict, and the Orleans drain resumes a recorded command
whose attempt failed — both would run a committed save's work again. The review of this change found
that; it is why the change reaches past the save. Evidence: the
implementation (`EventSource.cs`, `EventBundleOutboxDispatcher.cs`), and the review of #160, which
named both cases.

## Goals / Non-Goals

**Goals:**
- A caller, and the framework's own pipelines, can tell a save that committed from one that did not.
- An empty save costs nothing.

**Non-Goals:**
- Recovering the lost bundle. A host that cannot afford to lose one opts into durable bundles, whose
  row is committed with the events (`outbox-and-messaging`). The exception tells the caller to
  republish or replay; it does not do it.

## Decisions

**A dedicated exception type, not a flag on an existing one.** The failure has to be caught by type in
a retry predicate and in a handler, and `ConcurrencyException` and the persistence failures mean the
opposite (nothing was written).

**Wrap everything, cancellation included.** A cancellation after the commit leaves the same state
behind as any other failure. Surfacing it as a plain `OperationCanceledException` would make a
transport requeue the message and a stopping silo hand the command back, both of which run it again.

**Acknowledge in the transports, complete in the execution model.** The events are durable; what is
lost is their publication, which a republish or replay restores. Redelivery or resumption can only
add duplicates. Both log at error level so the lost publication is seen.
- *Alternative:* dead-letter the message. Rejected: an operator returning it would run it again.

**The empty save still runs its (empty) transaction and only skips the bundle.** The bundle is what a
reader sees. The empty transaction writes nothing, and keeping it leaves the save's shape unchanged for
anything observing the write store.

**The empty save still validates the session.** `A save … with no session context` is specified to
fail; checking before short-circuiting keeps that true.

## Risks / Trade-offs

- [Risk] A caller that caught the outbox's own exception type after a save now sees
  `CommittedEventsNotPublishedException` with that exception as the inner one. → The changelog says so;
  the inner exception is unchanged.
