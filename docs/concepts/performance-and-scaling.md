# Performance and Scaling

Event sourcing has a reputation for being slow — endless replay, polling read models,
reflection-heavy dispatch. Stratara is built the other way around: the hot paths avoid
reflection, projections are pushed rather than polled, and event streams partition
deterministically so workers scale out across nodes. This page explains the design choices
and backs the important ones with measured numbers.

## Reflection-free hot paths

The places a CQRS / event-sourcing framework runs millions of times — dispatching a command
to its handler, applying an event to an aggregate during replay, routing an event to a
projection or saga — never go through `MethodInfo.Invoke` or `Activator.CreateInstance`.
Each one is compiled **once** into a strongly-typed delegate (a `System.Linq.Expressions`
lambda) and cached for the lifetime of the process. The first call pays a one-time
compilation cost; every call after that is a direct invocation.

The same applies to property access used by optimistic-merge and the encryption serializer,
to event construction when reading from the store, and to the mediator's per-request handler
pipeline — all memoized in concurrent caches keyed by type.

## Push-driven projections — no polling

Read models in Stratara are **pushed**, not pulled. When the write side commits, it publishes
an `EventBundle` to the message bus; the projection worker is **subscribed** to that topic and
reacts to the bundle as it arrives. There is no "is there anything new?" table scan on an
interval, so there is no polling lag and no idle database load between events. Sagas consume
the same event stream the same way.

## Asynchronous by default

The default write path is fire-and-forget: an API endpoint hands a command to the
`ICommandOutboxDispatcher` and gets an envelope id back immediately (HTTP `202 Accepted`),
while a command-handling worker processes it out-of-process. The request thread never blocks
on the business logic, and load spikes are buffered by the bus rather than the web tier.
Reads (`IQuery<T>`) stay synchronous and side-effect-free. The whole stack is `async`/`await`
end to end — no `.Result`, no `.Wait()` on the hot path.

## Horizontal scaling via stream buckets

Every aggregate id maps deterministically to one of **4096 buckets** (a stable hash). The same
id always lands in the same bucket, which gives two things at once:

- **Single-writer-per-aggregate, parallelism between aggregates.** A per-bucket lock serializes
  writes to one aggregate while letting writes to different aggregates run fully in parallel —
  no global lock.
- **Indexed partition scans.** Events, snapshots, the command log, and the outbox all carry a
  bucket id and are indexed on it, so partition-scoped queries don't scan the whole table.

Because the partitioning is deterministic, you scale by adding workers — not by changing code.

## Competing-consumer workers

Command, projection, and saga workers run as **competing consumers**: any number of worker
instances on different machines, containers, or Kubernetes pods subscribe to the same topic,
and the broker (RabbitMQ or Azure Service Bus) balances the load across them automatically.
Each instance additionally fans out to `Environment.ProcessorCount` parallel subscriptions, so
a 16-core node runs 16 handlers per worker type. Scaling out is a deployment knob — more
replicas — not a rewrite. (The event-stream hashing worker is the one deliberate exception: it
runs single-instance because the hash chain is append-only.)

## Delivery guarantee — fast path plus durable fallback

The outbox dispatcher tries a **direct publish to the bus first** (the fast path). Only if the
bus is unreachable does it persist the message to the outbox table, where a retry worker
re-publishes it later and deletes it only after a confirmed send. Delivery is **at-least-once**:
the fast path keeps normal-case latency low, the durable fallback survives a broker outage, and
handlers stay idempotent (checkpoint the highest event version per stream, or de-duplicate on
the event id).

## Optimistic concurrency and snapshots

Concurrent writers are caught by a unique `(bucket, stream, version)` index — a losing writer
gets a `ConcurrencyException`, re-aggregates, and retries. To keep replay cheap on long streams,
the write store snapshots an aggregate automatically (every 50 events by default); a rebuild
loads the latest snapshot and replays only the delta after it.

## Measured numbers

The benchmarks below come from the `Stratara.Benchmarks` project (BenchmarkDotNet). Re-run any
of them with `dotnet run -c Release --project tests/Stratara.Benchmarks -- --filter '*'`.

```
BenchmarkDotNet v0.15.8 · macOS 26.5 (Darwin 25.5.0)
Apple M4, 10 logical / 10 physical cores · .NET 10.0.8, Arm64 RyuJIT
```

> **Read these as ratios, not server absolutes.** They were measured on a **fanless MacBook
> Air M4** — a passively-cooled laptop, not server hardware. A cooled production node produces
> different absolute numbers; the *relationships* hold.

### Replay scales linearly, on flat memory

In-memory aggregate replay through the compiled-expression apply-dispatch (excludes the database
read a real rehydration adds):

| Events replayed | Time | Allocated |
|---|---:|---:|
| 10,000 | 0.11 ms | 64 B |
| 100,000 | 1.13 ms | 64 B |
| 1,000,000 | 11.6 ms | **64 B** |

About 11 ns per event, and the allocation is **constant at 64 bytes** regardless of stream length
— a million-event aggregate rebuilds without producing per-event garbage.

### Reflection-free property access

The strongly-typed compiled delegate the framework uses internally, against reflection (with the
`PropertyInfo` pre-resolved, so this is pure invoke cost):

| Operation | Reflection | Compiled delegate | Speedup |
|---|---:|---:|---:|
| Property write | 6.04 ns | 0.47 ns | **~13×** |
| Property read | 3.86 ns | 0.62 ns | **~6×** |

Both allocation-free.

### Other hot paths

| What | Measured | Reading |
|---|---|---|
| **Tamper-evident chain hash** (~1 KB event) | **~0.4–0.7 µs / event** | Hardware-accelerated SHA-256 (~3 GB/s) — integrity costs sub-microsecond per event, even on the fanless laptop. |
| **Field-level encryption** (AES-GCM, per object) | **~4 µs** | Real per-object crypto; an object with *no* `[EncryptData]` fields adds only **~45 ns** over plain `System.Text.Json` — you pay only for what you actually encrypt. |
| **Source-generated logging** (level disabled) | **~0.5 ns, 0 B** | A `[LoggerMessage]` call short-circuits before formatting or boxing when its level is off. Classic interpolated logging allocates regardless of level. |

A note on honesty: the **~13×** figure is the strongly-typed delegate the framework actually uses.
A *by-name* accessor (the convenience wrapper that resolves the property from a string each call)
measures only ~1.2× faster than reflection — still faster and allocation-free, but not the same
win. We quote the path the framework runs on, and the number it actually produces.

## See also

- [Tamper-Evident Streams](tamper-evident-streams.md) — the hash chain behind the integrity number above.
- [Why Event Sourcing](why-event-sourcing.md) — what append-only buys you, and its trade-offs.
- [Architecture at a glance](../overview/architecture-at-a-glance.md) — where the workers and stores sit.
