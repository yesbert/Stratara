## Context

`EventSource.SaveChangesAsync` prepares the bundle, commits entries, snapshots and (durable mode) the
bundle row in one transaction, then calls `IEventBundleOutboxDispatcher.EnqueueEventBundleAsync`. In
non-durable mode the RabbitMQ and Azure Service Bus dispatchers try the bus and fall back to an
outbox row in a separate transaction; a failure of that write propagates. Evidence: the
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

**Wrap everything but cancellation.** A cancellation after the commit leaves the same state behind. But
a cancelled operation is expected to surface as `OperationCanceledException`, and retry pipelines do
not retry cancellation anyway.

**The empty save still validates the session.** `A save … with no session context` is specified to
fail; checking before short-circuiting keeps that true.

## Risks / Trade-offs

- [Risk] A caller that caught the outbox's own exception type after a save now sees
  `CommittedEventsNotPublishedException` with that exception as the inner one. → The changelog says so;
  the inner exception is unchanged.
