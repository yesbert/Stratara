# Stratara.Sample.AspNetCoreApi

Sample #5 of the learning path — and the only web-shaped one. Wires HTTP endpoints to the same `IMediator` surface from sample #1 using ASP.NET Core minimal APIs. Same bank-account domain, just a different driver.

Conceptually parallel to `CqrsBasics` (you can read it without going through #2–#4) — it just answers the question "what does the HTTP layer look like in a Stratara host?".

## The wire-up rule

Endpoints **never** call handlers directly. They go through `IMediator` (or, in real Stratara hosts, `ICommandOutboxDispatcher` for async commands). The endpoint method is responsible for:

1. Translating the HTTP request body into a command/query record.
2. Awaiting `mediator.HandleAsync(...)`.
3. Mapping the result (or domain exception) to an `IResult`.

That's the whole convention — encoded in [`Endpoints/AccountEndpoints.cs`](Endpoints/AccountEndpoints.cs).

## Endpoint map

| Method | Path | Mediator dispatch | Success | Failure |
|---|---|---|---|---|
| `POST` | `/accounts` | `OpenAccountCommand : ICommand<Guid>` | `201 Created` + `{id}` | — |
| `POST` | `/accounts/{id}/deposits` | `DepositCommand : ICommand` | `204 No Content` | — |
| `POST` | `/accounts/{id}/withdrawals` | `WithdrawCommand : ICommand` | `204 No Content` | `409 Conflict` (RFC 9110 ProblemDetails) on `InsufficientBalanceException` |
| `GET` | `/accounts/{id}/balance` | `GetBalanceQuery : IQuery<decimal>` | `200 OK` + `{accountId, balance}` | — |

## What to look at, in order

1. **`Program.cs`** — `WebApplication.CreateBuilder` instead of `Host.CreateApplicationBuilder`, otherwise identical DI: repository, `TimeProvider`, OTel tracer, `AddMediator` + handler discovery. One `app.MapAccountEndpoints()` and `app.Run()`.

2. **`Endpoints/AccountEndpoints.cs`** — one `IEndpointRouteBuilder` extension method, four lambdas, two request DTOs (`OpenAccountRequest`, `AmountRequest`). The `WithdrawCommand` endpoint shows the recommended pattern for mapping domain exceptions to HTTP problem responses.

3. **`Commands/`, `Queries/`, `Domain/`, `Infrastructure/`** — identical to `Stratara.Sample.CqrsBasics`. Pure copy/paste with the namespace rewritten so each sample reads from top to bottom in isolation.

## Run it

```bash
dotnet run --project samples/Stratara.Sample.AspNetCoreApi
```

The host binds to whatever Kestrel picks for its default port (look at the launch logs — typically `http://localhost:5000` or similar). Then in another terminal:

```bash
# Open account
curl -X POST http://localhost:5000/accounts \
  -H "Content-Type: application/json" \
  -d '{"ownerName":"Alice","initialBalance":100}'
# → 201 {"id":"..."}

# Deposit
curl -X POST http://localhost:5000/accounts/<id>/deposits \
  -H "Content-Type: application/json" \
  -d '{"amount":50}'
# → 204

# Balance
curl http://localhost:5000/accounts/<id>/balance
# → 200 {"accountId":"...","balance":150}

# Over-withdraw
curl -X POST http://localhost:5000/accounts/<id>/withdrawals \
  -H "Content-Type: application/json" \
  -d '{"amount":999}'
# → 409 {"title":"Insufficient balance",...}
```

## Where this would extend in a real host

| Concern | This sample | Real Stratara |
|---|---|---|
| Async commands | sync via `IMediator` | usually `ICommandOutboxDispatcher.EnqueueCommandAsync` → `202 Accepted` (see sample #3) |
| AuthN/Z | none | `[RequireRole]` + `AuthorizingMediator` |
| Session context | none | `SessionContextMiddleware` from `Stratara.Sessions` populates Actor + Subject from JWT |
| Health checks | none | `Stratara.ServiceDefaults.AspNetCore`'s `AddDefaultHealthChecks()` + `MapDefaultEndpoints()` |
| OpenAPI | none | `Microsoft.AspNetCore.OpenApi` + `Scalar.AspNetCore` |
| OTel | NoOp tracer | full OTel via `ServiceDefaults` (consumer host adds it) |

This sample focuses on the **endpoint-to-mediator** routing only — the surrounding ASP.NET concerns are real-host setup that doesn't change the dispatch contract.
