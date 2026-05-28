# Sample 3 — Outbox + Worker

**Concept**: Outbox + message bus + two background workers (async dispatch). What changes when commands stop running in the caller's thread.

- **Code**: [`samples/Stratara.Sample.OutboxWorker`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.OutboxWorker)
- **Lines**: ~300
- **Read time**: 15–20 min
- **Prerequisite**: [Sample 2 — Event Sourced](02-event-sourced.md).

## What you'll see

1. **`InMemoryOutbox`** — the sample's stand-in for the `outbox_entry` table. Same semantics as the EF Core impl.
2. **`InMemoryMessageBus`** — pub/sub on top of a `Channel<>`. Stands in for RabbitMQ / Azure Service Bus.
3. **Two `IHostedService`s** running concurrently:
   - **OutboxWorker** — polls the outbox, publishes pending commands to the bus.
   - **CommandWorker** — subscribes to the bus, deserializes commands, dispatches them via `IMediator` to handlers.
4. **Asynchronous semantics** — the caller doesn't `await` the handler. The producer is the *thing that enqueues to the outbox*; the consumer is the *thing that picks it up later*.

## Running

```bash
dotnet run --project samples/Stratara.Sample.OutboxWorker
```

Expected output (abridged):

```
=== Stratara Outbox + Worker ===

--- Publisher enqueues 3 commands (returns immediately, doesn't wait for handlers) ---
  Enqueued — outbox has 3 pending

--- Wait for outbox-drain + command-worker to catch up ---
  Outbox now has 0 pending

--- Read-side: query the repository synchronously ---
  Balance: $175.00

--- Enqueue WithdrawCommand $40, wait, query again ---
  Balance: $135.00

Done.
```

## What changed vs. Sample 2

| Sample 2 (sync event-sourced) | Sample 3 (async via outbox) |
|---|---|
| `mediator.HandleAsync(cmd)` runs the handler in the caller's thread | `dispatcher.EnqueueAsync(cmd)` returns immediately after appending to the outbox |
| Handler exceptions bubble back to the caller | Handler exceptions are caught + the outbox-entry retried (with backoff) |
| Strict ordering — caller controls when the next command runs | At-least-once delivery — consumers must be idempotent |

## What's missing (covered by later samples)

- **No fan-out** — every command is processed by exactly one handler. [Sample 4](04-money-transfer-saga.md) shows one event fanning into two commands via a saga.
- **No HTTP** — there's no API in front. [Sample 5](05-aspnetcore-api.md) puts an ASP.NET minimal-API on top.
