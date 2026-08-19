# Stratara.Testing.EntityFrameworkCore

> **Derived.** The behaviour described here is specified under `openspec/specs/`. Those
> specifications are the source; this page explains and illustrates them.

Spin up the **real** Stratara event-sourcing write stack — `IEventSource`, `IAggregationService`,
snapshots, and the EF Core write store — against a shared **in-memory SQLite** database, in one
call. You exercise production code paths (real serialization, real version tracking, real unique
constraints) without Postgres or Docker.

Builds on [`Stratara.Testing`](https://www.nuget.org/packages/Stratara.Testing): the cross-cutting
dependencies are wired with its in-memory doubles (`InMemoryKeyStore`, `TestSessionContextProvider`).

## Why not a hand-rolled in-memory `IEventSource`?

Because a bespoke fake would drift from production (subject resolution, concurrency detection,
outbox dispatch, snapshots). This package runs the genuine `EventSource` on SQLite instead, so your
tests verify the real behavior.

## Example

```csharp
await using var host = EventStoreTestHost.Create(s =>
    s.AddAggregatesFromAssemblyContaining<Account>());

await host.ExecuteAsync(async events =>
{
    await events.CreateAsync<Account>(id, new AccountOpened(id, tenantId, "Ada", 100m));
    await events.AppendAsync<Account>(id, new AmountWithdrawn(30m));
    await events.SaveChangesAsync();
});

var account = await host.AggregateAsync<Account>(id);
Assert.Equal(70m, account!.Balance);
Assert.Single(host.Outbox.Bundles);   // the SaveChanges emitted one bundle
```

## Contents

- `EventStoreTestHost` — owns a shared open SQLite connection + a configured service provider;
  exposes `ExecuteAsync(IEventSource)`, `AggregateAsync<T>(streamId)`, the preset `Session`, and the
  recording `Outbox`. `IAsyncDisposable`.
- `AddStrataraTestingEventStore<TWriteDbContext>(connection, tenantId)` — the lower-level DI
  extension if you compose the provider yourself.
- `StrataraTestWriteDbContext` — a ready-made concrete write context (no subclass boilerplate).
- `RecordingEventBundleOutboxDispatcher` — captures emitted bundles for assertions.

## Notes

- The SQLite connection is `:memory:` and shared across every DbContext the unit of work mints — it
  must stay open for the host's lifetime (the host manages this; dispose it when done).
- Register your aggregates (`AddAggregatesFromAssemblyContaining<T>()`) so event payload types
  deserialize on rehydration.

## Dependencies

- `Stratara.Testing`, `Stratara.Infrastructure`, `Stratara.EventSourcing.EntityFrameworkCore`,
  `Stratara.Shared`, `Stratara.Abstractions`, `Stratara.Contracts`
- `Microsoft.EntityFrameworkCore.Sqlite`

Reference it from test projects only.
