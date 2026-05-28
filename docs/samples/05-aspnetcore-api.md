# Sample 5 — ASP.NET Core API

**Concept**: HTTP minimal-API endpoints wired straight to the mediator. Parallel to Sample 1 — pick this one if you want an HTTP front instead of a console.

- **Code**: [`samples/Stratara.Sample.AspNetCoreApi`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.AspNetCoreApi)
- **Lines**: ~250
- **Read time**: 10–15 min
- **Prerequisite**: any of Samples 1–4. This one is parallel — readable at any point.

## What you'll see

1. **`WebApplication.CreateBuilder(args)`** + `app.MapAccountEndpoints()` — a vanilla minimal-API.
2. Each endpoint delegates to `IMediator.HandleAsync(...)` — **no business logic in the endpoint**.
3. **HTTP status codes mapped from the handler's outcome**: `201 Created` from `OpenAccountCommand`, `204 NoContent` from `DepositCommand`, `409 Conflict` from `InsufficientBalanceException`.
4. The `RFC 7807 ProblemDetails`-style error response uses ASP.NET's built-in `Results.Problem(…)`.

## Endpoints

| Verb | Route | Handler |
|---|---|---|
| `POST` | `/accounts` | `OpenAccountCommand` |
| `POST` | `/accounts/{id}/deposits` | `DepositCommand` |
| `POST` | `/accounts/{id}/withdrawals` | `WithdrawCommand` (returns 409 on `InsufficientBalanceException`) |
| `GET` | `/accounts/{id}/balance` | `GetBalanceQuery` |

## Running

```bash
dotnet run --project samples/Stratara.Sample.AspNetCoreApi
# Now listening on: http://localhost:5000
```

Try it:

```bash
ID=$(curl -sS -X POST http://localhost:5000/accounts \
  -H 'Content-Type: application/json' \
  -d '{"ownerName":"Alice","initialBalance":100}' \
  | jq -r .id)

curl -sS -X POST http://localhost:5000/accounts/$ID/deposits \
  -H 'Content-Type: application/json' \
  -d '{"amount":50}'

curl -sS http://localhost:5000/accounts/$ID/balance
# {"accountId":"…","balance":150}
```

## Why this layout

- **Endpoint = mediator dispatch + HTTP-status translation.** Nothing more. The endpoint never opens a DbContext, never speaks to the bus, never knows that `OpenAccountCommand` returns a `Guid`.
- **The mediator pipeline owns** auth, validation, audit, retry. The endpoint is just the HTTP-shape adapter.
- This makes the same handler reusable from a console host, a worker, a gRPC service, or a CLI — without changing the handler.

## What's missing

- **No outbox** — for this sample, all commands run in-process. A production HTTP host would typically use the **synchronous mediator for queries + creation flows** (where the caller needs the result immediately) and **`ICommandOutboxDispatcher` for fire-and-forget mutations** (where async + at-least-once delivery is the right contract).
- **No identity / authorization** — see [Auth Decorators guide](../guides/auth-decorators.md) to add `[RequireRole]`.
