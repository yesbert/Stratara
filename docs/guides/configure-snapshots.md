# Configure Snapshots

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
