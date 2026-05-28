# Stratara.Sample.EventSourced

Sample #2 of the learning path. Replaces the in-memory aggregate from [`Stratara.Sample.CqrsBasics`](../Stratara.Sample.CqrsBasics) with **event sourcing** + a **separate read-side projection**. Same bank-account domain, same mediator surface, but the state lives in an append-only event stream.

## What's new versus `CqrsBasics`

| Concern | `CqrsBasics` | `EventSourced` |
|---|---|---|
| Aggregate state | mutable POCO, kept in a repository | rebuilt by replaying events through `Apply(TEvent)` methods |
| What gets persisted | the aggregate snapshot | the **events** (3 sealed records: `AccountOpened`, `AmountDeposited`, `AmountWithdrawn`) |
| Read model | the same aggregate | a separate `AccountBalanceProjection` (in-memory dict, updated on each event) |
| Query path | reads the aggregate | reads the projection |
| Write-side invariant check | mutates the aggregate (which throws) | rebuilds the aggregate via `AggregationService` then validates before appending the event |

## What to look at, in order

1. **`Domain/AccountEvents.cs`** — three `sealed record` events. They carry the `AccountId` so a projection can route them without needing an `IEvent` wrapper.

2. **`Domain/Account.cs`** — the aggregate. `IAggregate` marker; public setters (Stratara convention — snapshot deserialisation needs them); one `Apply(TEvent)` method per event type. **No mutating "behaviour" methods** — behaviour is in command handlers, state derives from events.

3. **`EventStore/InMemoryEventStore.cs`** — a tiny facade over a `Dictionary<Guid, List<object>>`. Demonstrates the lifecycle: `Append` queues, `SaveChangesAsync` commits the queue to the stream *and* dispatches the events to every registered projection. In real Stratara, `IEventSource` is more elaborate (see `Stratara.Abstractions/Abstractions/EventSourcing/IEventSource.cs`) — concurrency control, session-context Subject derivation, `IAggregateCreationEvent` handling, outbox coupling — but the lifecycle shape is the same.

4. **`EventStore/AggregationService.cs`** — reflection-based aggregate rebuild. Iterates the stream's events and dispatches each to the matching `Apply(TEvent)` on a fresh aggregate instance. This is exactly what Stratara's `IAggregationService` does, minus the type-resolver / snapshot acceleration.

5. **`Projections/AccountBalanceProjection.cs`** — the read-side. Listens to all three events, materialises an `AccountBalanceView` per account. Queries read this, never the aggregate.

6. **`Commands/`** — the handlers append events instead of mutating state. `WithdrawCommandHandler` rebuilds the aggregate via `AggregationService` to check the balance invariant *before* appending the event. (`OpenAccountCommand`'s handler doesn't need to rebuild — there is no prior state.)

7. **`Queries/GetBalanceQuery.cs`** — returns the projection view, never touches the event stream or the aggregate.

8. **`Program.cs`** — DI wiring + demo script. At the end it dumps the raw event stream so you can see what's actually stored: three events for a one-deposit-two-deposit-one-withdraw history.

## Run it

```bash
dotnet run --project samples/Stratara.Sample.EventSourced
```

You'll see the projection report `$135` after `100 + 50 + 25 − 40`, a rejected over-withdraw, and then the underlying event stream printed verbatim.

## Where to go next

- **`Stratara.Sample.OutboxWorker`** — push commands through an outbox so they're handled by a separate worker (async).
- **`Stratara.Sample.MoneyTransferSaga`** — coordinate `Withdraw` + `Deposit` across two accounts as a saga.

## How this maps to the real Stratara

The Sample uses a hand-rolled `InMemoryEventStore` + `AggregationService` so it has zero external dependencies. In a real Stratara host you'd compose:

| Sample type | Real Stratara |
|---|---|
| `InMemoryEventStore` | `IEventSource` (from `Stratara.Abstractions`) — implemented by `Stratara.EventSourcing.EntityFrameworkCore` on top of PostgreSQL |
| `AggregationService` | `IAggregationService` (same package) |
| `IProjection` + manual dispatch | `IProjection` + `IProjectionHandler` + `IProjectionDispatcher` in `Stratara.Projections`, wired into an event-projection worker |
