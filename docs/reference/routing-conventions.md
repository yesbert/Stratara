# Routing Conventions

The full decision matrix for which marker type a command/query should implement and which dispatcher to invoke.

## The matrix

| Scenario | Marker | Dispatcher | Why |
|---|---|---|---|
| Mutation without result (default) | `ICommand` | `ICommandOutboxDispatcher` | Async, at-least-once via outbox + bus |
| Mutation with synchronous result needed (UI feedback, setup flows, "create-and-return-id") | `ICommand<T>` | `IMediator` | In-process, caller awaits the result |
| Mutation that destroys infrastructure (drop outbox table, recreate write-store) | `ICommand<T>` | `IMediator` | Cannot route through the very outbox you're destroying |
| Mutation that signals process-local state (toggle a debug flag, force a cache flush) | `ICommand` | `IMediator` | No need for cross-process dispatch |
| Read-only data | `IQuery<T>` | `IMediator` | Reads have no business going through the outbox |

## Forbidden patterns

- **`IQuery<T>` with side effects.** Queries are read-only by contract. If your "query" mutates state, it's a command — re-mark it.
- **HTTP endpoints calling handlers directly.** Always go through `IMediator` or `ICommandOutboxDispatcher`. Direct handler-calls bypass the pipeline (no auth, no audit, no retry).
- **Saga `await`ing an outbox dispatch.** `ICommandOutboxDispatcher.EnqueueAsync(...)` returns when the row is *written*, not when the handler ran. If you need to wait for the result, you're in `IMediator` territory.

## How to pick: a quick decision

```
Does the command return a value?
├── No
│   ├── Is it OK if it runs minutes from now? ────────── ICommand          via OutboxDispatcher
│   └── Must it run now (process-local signal)? ──────── ICommand          via Mediator
└── Yes ──────────────────────────────────────────────── ICommand<TResult> via Mediator
```

For reads, always `IQuery<T>` via `Mediator`.

## When this is enforced

- The `Stratara.Mediator.AuthorizingMediator` enforces `[RequireRole]` at the marker level — `IMediator.HandleAsync(...)` checks every command/query.
- `AuthorizationStartupValidator` walks every registered handler at host-start and verifies the role-check chain is intact (`IAuthorizingMediator` marker, since v3.0.2). Hosts with a broken decorator chain fail-fast.

## Consumer convention

The same routing convention can be mirrored on the consumer side. A typical pattern: the consumer's command-bus layer wires the **default** mutation path through the outbox; UI-driven mutations that need a sync response (e.g. "Create Tenant" returning the new ID) use `IMediator` directly.
