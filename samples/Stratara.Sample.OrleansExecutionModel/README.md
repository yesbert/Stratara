# Stratara.Sample.OrleansExecutionModel

> **Derived.** The behaviour described here is specified under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them.

Shows the **Orleans execution model** end to end in one console run — a command in its aggregate's
activation, a projection that reads the store in commit order, and a stateful process whose durable
timeout fires a second later — on `ExecutionModelTestHost` from **`Stratara.Testing.Orleans`**: one
silo in the process, reminders and the grain directory in memory, the write stack on in-memory SQLite.
No cluster, no broker, no database server.

## What to look at, in order

1. **`Domain.cs`** — the `Account` aggregate, the `OpenAccount` command that names it, a projection
   and a `SagaProcess<TState>` that schedules a timeout. Nothing in them knows about Orleans.
2. **`Program.cs`** — the registrations a production silo makes (`AddStrataraAggregateGrains`,
   `AddStrataraProjectionGrains`, `AddStrataraSagaGrains`), then a dispatch, the wait for the readers,
   and the wait for the timeout.

## Run it

```bash
dotnet run --project samples/Stratara.Sample.OrleansExecutionModel
```

Expected output: the command is handled in the aggregate's activation, the projection applies Ada's
balance of 100.00, and the process's timeout fires about a second later.

## Where the sample stops

The test host is one silo and keeps everything in memory, which is why the project sets
`StrataraAllowTestSupportOutsideTests` — the documented exception for a sample. A production silo
composes the same registrations against a real cluster, reminder table, grain directory and store:
see the [migration guide](../../docs/guides/migrate-to-the-orleans-execution-model.md). What happens
across silos — a kill, a role split, a takeover — needs real infrastructure and is not shown here.
