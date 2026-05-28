# Stratara.Sample.OutboxWorker

Sample #3 of the learning path. Pushes commands through an **outbox** + **message bus** + **worker** chain — the asynchronous, decoupled dispatch path. Same bank-account domain, same mediator surface, but now the call site `Enqueue`s and returns immediately; a hosted service in the same process picks up the command later and runs the handler.

## What's new versus `CqrsBasics`

| Concern | `CqrsBasics` | `OutboxWorker` |
|---|---|---|
| Dispatch | `mediator.HandleAsync(cmd)` synchronous | `dispatcher.Enqueue(cmd)` returns immediately |
| Handler runs… | inline on the calling thread | on a background worker, after the bus delivers the message |
| Decoupling | none — caller blocks on the handler | full — caller can crash, worker still processes |
| Failure modes | exceptions propagate to caller | persisted outbox entry until successful publish |

## The pipeline

```
caller ──► CommandOutboxDispatcher ──► InMemoryOutbox (queue)
                                               │
                                               ▼
                                       OutboxDrainWorker (background loop, 50ms tick)
                                               │
                                               ▼
                                         InMemoryMessageBus (Channel<object>)
                                               │
                                               ▼
                                  MediatorCommandWorker (background subscriber)
                                               │
                                               ▼
                                            IMediator
                                               │
                                               ▼
                                         ICommandHandler<T>
```

Two hosted services run for the lifetime of the host: `OutboxDrainWorker` polls the outbox and publishes pending entries to the bus, `MediatorCommandWorker` is subscribed to the `commands` topic and dispatches each delivered `OutboxEntry` to the in-process `IMediator`.

## What to look at, in order

1. **`Outbox/OutboxEntry.cs`** — the durable representation. `Id`, the `CommandTypeName` (assembly-qualified, so the worker can re-resolve the type), the `PayloadJson` (System.Text.Json), and a timestamp. Stratara's real `OutboxEntry` adds bucketing + row-versioning columns for the EF Core write store; the shape is otherwise the same.

2. **`Outbox/CommandOutboxDispatcher.cs`** — the public dispatch surface. Serialises the command + writes the outbox entry. **No bus publish** at the call site — that's the drain worker's job. (Stratara's real dispatcher tries a fast-path bus publish first and only falls back to the outbox on failure; the sample skips that for clarity.)

3. **`Workers/OutboxDrainWorker.cs`** — `BackgroundService`, ticks every 50ms, drains pending entries → `IMessageBus.PublishAsync`. The drain is at-least-once: an entry stays in the queue until publish succeeds.

4. **`Messaging/InMemoryMessageBus.cs`** — implements Stratara's `IMessageBus` (`PublishAsync` / `SubscribeAsync`) over `System.Threading.Channels`. One unbounded channel per topic.

5. **`Workers/MediatorCommandWorker.cs`** — the read-side. Subscribes to the `commands` topic, deserialises each `OutboxEntry` back into its concrete command type via reflection, opens a DI scope, and dispatches through `IMediator.HandleAsync<TCommand>`. The reflective `MakeGenericMethod` trick is exactly what Stratara's `MediatorCommandWorker` does too.

6. **`Commands/`** — `OpenAccountCommand`, `DepositCommand`, `WithdrawCommand`. All are `ICommand` (no result) — async dispatch doesn't fit `ICommand<TResult>` because the caller doesn't wait. `OpenAccountCommand` carries the `AccountId` (the publisher generates it client-side).

7. **`Program.cs`** — wires everything up, starts the host, enqueues three commands, waits 500 ms for the workers to catch up, queries the balance synchronously through `IMediator`.

## Run it

```bash
dotnet run --project samples/Stratara.Sample.OutboxWorker
```

Expected: outbox holds 3 entries momentarily, then drops to 0 as the drain + worker chain executes. Balance reads $175 (100 + 50 + 25), then $135 after the withdraw.

## How this maps to the real Stratara

| Sample type | Real Stratara |
|---|---|
| `InMemoryOutbox` | `IOutboxRepository` over EF Core (`outbox_entry` table) |
| `CommandOutboxDispatcher` | `ICommandOutboxDispatcher` from `Stratara.Abstractions`, implemented in `Stratara.Outbox.RabbitMQ` |
| `OutboxDrainWorker` | `OutboxWorker` hosted service in `Stratara.Outbox.RabbitMQ` |
| `InMemoryMessageBus` | `RabbitMqBus` (dev) or `ServiceBus` (Azure prod) in `Stratara.Outbox.RabbitMQ/Messaging/` |
| `MediatorCommandWorker` | `MediatorCommandWorker` in the same package |

The composition is the same: `services.AddOutboxWorker(builder.Configuration)` + `services.AddMediatorWorker()` + `services.AddMessaging()` wires the whole production stack.

## Where to go next

- **`Stratara.Sample.MoneyTransferSaga`** — drives `WithdrawCommand` and `DepositCommand` across two accounts as a saga.
- **`Stratara.Sample.AspNetCoreApi`** — same dispatch surface, but driven from HTTP endpoints.
