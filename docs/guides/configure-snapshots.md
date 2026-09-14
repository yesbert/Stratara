---
title: "Configure Snapshots"
description: "When replaying a stream from the start stops being free, how snapshots shorten it, and the policy that decides when one is written."
---

# Configure Snapshots

> **Derived page.** The behaviour described here is specified by the `aggregate-rehydration` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

An event-sourced aggregate is rebuilt by replaying its event stream from the start. A **snapshot**
is a cached materialisation of the aggregate at a given version: when one exists, Stratara loads the
snapshot and replays only the events appended *after* it, instead of the whole history. The longer a
stream gets, the more snapshots matter for load latency.

Stratara writes snapshots automatically on save. *When* it writes them is decided by an
`ISnapshotStrategy`, which you can override.

## The default

`AddEventSourcing()` registers `VersionThresholdSnapshotStrategy` with a threshold of **50**: a
stream is snapshotted once it has advanced by at least 50 versions since its most recent snapshot,
for every aggregate type. You don't need to register anything — this is the out-of-the-box behaviour
and is identical to every prior Stratara release.

## The contract

`ISnapshotStrategy` lives in `Stratara.Abstractions.EventSourcing`:

```csharp
public interface ISnapshotStrategy
{
    bool ShouldSnapshot(Type aggregateType, long currentVersion, long lastSnapshotVersion);
}
```

The runtime calls it once per stream that just had events appended. `lastSnapshotVersion` is `0`
when the stream has no snapshot yet. Return `true` to write a fresh snapshot at `currentVersion`.

## Change the uniform cadence

Keep the one-threshold-for-everything policy but change how often it fires:

```csharp
// Snapshot every 200 versions instead of 50.
builder.Services.AddSingleton<ISnapshotStrategy>(new VersionThresholdSnapshotStrategy(threshold: 200));
```

## Vary the cadence per aggregate type

Large, hot streams benefit from snapshots; tiny, short-lived aggregates don't. Decide per type:

```csharp
public sealed class PerAggregateSnapshotStrategy : ISnapshotStrategy
{
    public bool ShouldSnapshot(Type aggregateType, long currentVersion, long lastSnapshotVersion)
    {
        var distance = currentVersion - lastSnapshotVersion;
        return aggregateType.Name switch
        {
            "Conversation" => distance >= 200,  // long-lived, snapshot less often
            "Tenant"       => false,            // tiny aggregate, never snapshot
            _              => distance >= 50,   // framework default for everything else
        };
    }
}

builder.Services.AddSingleton<ISnapshotStrategy, PerAggregateSnapshotStrategy>();
```

## Disable snapshots entirely

Register `NoSnapshotStrategy`. Streams are then always rebuilt from their full event history — useful
for tests, short-lived aggregates, or deployments where snapshot storage is undesirable:

```csharp
builder.Services.AddSingleton<ISnapshotStrategy, NoSnapshotStrategy>();
```

## Registration order

`AddEventSourcing()` contributes the default strategy via `TryAddSingleton`, and the framework
resolves the **last-registered** `ISnapshotStrategy`. A custom registration therefore wins whether it
runs before or after `AddEventSourcing()` — you don't have to think about ordering.

## What a snapshot stores

`VersionThresholdSnapshotStrategy` and any custom strategy only decide *whether* to snapshot. The
snapshot itself is always the full aggregate state, serialised tenant-scoped through the same
encrypting serializer used for events (`ISecureJsonSerializer`, tenant AAD), so a snapshot is no less
protected than the events it summarises.

## What a rebuild applies, and what it skips

A rebuild — from the start or from a snapshot — hands each event to the aggregate's public `Apply`
method for that event's type: `Apply(TEvent)` taking the payload, or `Apply(IEvent<TEvent>)` taking
the envelope when the aggregate also needs the event's metadata. An event the aggregate declares
**no** `Apply` for is skipped, and the rebuild carries on with the rest of the stream:

```csharp
public sealed class Wallet : IAggregate
{
    public Guid Id { get; set; }

    public decimal Balance { get; set; }

    public void Apply(AccountOpened @event)
    {
        Id = @event.AccountId;
        Balance = @event.InitialBalance;
    }

    public void Apply(AmountDeposited @event) => Balance += @event.Amount;

    // No Apply(AmountWithdrawn): a withdrawal in this stream is skipped on rebuild, not rejected.
}
```

That is what lets an aggregate ignore facts it does not care about, and what keeps an old stream
replayable after you retire an event type from the aggregate's interest — delete the `Apply`, and the
events already recorded are simply passed over. The same rule is why a mistyped parameter or an
`Apply` that is not public goes unnoticed: nothing fails, the event just has no effect. A test that
rebuilds from a recorded stream is what catches it.

## Rebuild an aggregate as it stood

`IAggregationService.AggregateAsync<TAggregate>` takes an optional `toVersion`, inclusive. Bound it,
and the result is the aggregate as it was at that version — only the events up to and including it
are applied:

```csharp
public sealed class WalletHistory(IAggregationService aggregates)
{
    public Task<Wallet?> AsOfAsync(Guid walletId, long version, CancellationToken ct) =>
        aggregates.AggregateAsync<Wallet>(walletId, toVersion: version, cancellationToken: ct);
}
```

Leave `toVersion` out, and you get the stream's head. A stream that does not exist returns `null`.
Snapshots take part here too, and still do not change the result: a bounded rebuild starts only from
a snapshot taken **at or below** the bound, so asking for version 120 of a stream snapshotted at 100
and 150 starts from 100 and applies 101 to 120. The version to ask for is the one each event carries
on `IEvent.Version`.

When the aggregate type is known only at runtime — replay tooling, a generic admin endpoint — the
non-generic `AggregateAsync(Type aggregateType, Guid streamId, …)` overload takes the same bound and
returns the aggregate as `object`.
