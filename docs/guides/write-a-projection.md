---
title: "Write a Projection"
description: "How to turn an event stream into a read model, how Stratara discovers projection handlers, and what per-aggregate ordering does and does not promise."
---

# Write a Projection

> **Derived page.** The behaviour described here is specified by the `projections` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

A projection turns an event stream into a read model. `Stratara.Projections` discovers your
projections at startup, matches each incoming event bundle against the events a projection cares
about, and invokes only the matching methods.

`IProjection` (`Stratara.Projections.Abstractions`) is an **empty marker** — it declares nothing.
The contract is a naming convention: the runtime reflects over your class for `HandleAsync` methods
whose first parameter is the event payload or an `IEvent<TEvent>`. That method's event type is what makes a projection
"interested" in an event; events outside that set are skipped without invoking the projection.

## The shape

```csharp
using JetBrains.Annotations;
using Stratara.Abstractions.EventSourcing;
using Stratara.Projections.Abstractions;

public sealed class AccountBalanceProjection(IAccountBalanceStore store) : IProjection
{
    [UsedImplicitly]
    private Task HandleAsync(IEvent<AccountOpened> @event, CancellationToken ct) =>
        store.UpsertAsync(@event.Data.AccountId, @event.Data.InitialBalance, ct);

    [UsedImplicitly]
    private Task HandleAsync(IEvent<AmountDeposited> @event, CancellationToken ct) =>
        store.AddAsync(@event.Data.AccountId, @event.Data.Amount, ct);

    [UsedImplicitly]
    private Task HandleAsync(IEvent<AmountWithdrawn> @event, CancellationToken ct) =>
        store.AddAsync(@event.Data.AccountId, -@event.Data.Amount, ct);
}
```

You write one `HandleAsync(IEvent<TEvent>, CancellationToken)` per event you care about — no manual
`switch`, no base-method override. The payload is `@event.Data`: `IEvent<TEvent>` re-declares `Data`
as the typed event, while the non-generic `IEvent` also carries `StreamId`, `Version`, `TenantId`
and `UserId`.

### Payload or envelope

A handler may take the enveloped event, as above, or the **payload itself**. Take the payload when
the event is all you need:

```csharp
using JetBrains.Annotations;
using Stratara.Projections.Abstractions;

public sealed class OpeningBalanceProjection(IAccountBalanceStore store) : IProjection
{
    [UsedImplicitly]
    private Task HandleAsync(AccountOpened opened, CancellationToken ct) =>
        store.UpsertAsync(opened.AccountId, opened.InitialBalance, ct);
}
```

Take `IEvent<TEvent>` when you need the metadata as well — the stream, the version, the owning
tenant and user. When a relevant event arrives, the handler taking its payload is invoked; the one
taking the envelope is invoked only where the projection declares no payload handler for that event.
Declare one or the other per event, not both. Registration treats the two shapes alike: the payload
type is what `AddProjectionsFromAssemblyContaining<T>()` adds to the trusted types either way.

Handlers may be **private** — discovery uses `BindingFlags.NonPublic`, so they stay off the
projection's public surface. Mark them `[UsedImplicitly]` so analyzers don't flag them; the runtime
is the only caller. Where you store the read model is your choice — a Postgres table via your own
DbContext, an in-memory dictionary, an Elasticsearch document. `Stratara.Projections`' own
`TenantProjection` is written exactly this way and is the canonical example.

## Register it

```csharp
builder.Services
    .AddProjectionWorker(builder.Configuration)                       // runtime + hosted service
    .AddProjectionsFromAssemblyContaining<AccountBalanceProjection>(); // your IProjection types
```

Both calls matter. `AddProjectionsFromAssemblyContaining<T>()` registers the projections **and** the
event types they consume (so payloads deserialize); `AddProjectionWorker(IConfiguration)` registers
the manager, the method invoker, and the hosted service that consumes the event-bundle subscription.
Most hosts take both from the `AddEventProjectionWorkerServices()` composite in
`Stratara.EventSourcing.WorkerDefaults`.

**Events no projection handles are not read.** A bundle, a replay or a store reader meets events
that no projection in the host has a use for — types retired from an aggregate, framework events
another host reads, facts of streams this host was never meant to understand. The worker, the replay
and the Orleans reader decide from the event's type, after upcasting, whether any projection in the
host handles it, and leave every other event unread: not resolved, not decrypted, and so not in need
of a registration. The one exception keeps a moved type loud: an event whose type does not resolve,
but whose name without namespace or assembly is the name of a type a projection handles, is read and
fails as an unregistered type does. A handled type *renamed* without an upcaster cannot be told
apart from an unrelated event, and is left unread — add the upcaster when you rename one. A host that
replaces `IEventMapperFactory` with its own keeps reading every event, as before.

### Event-only hosts (no handler dependencies)

A projection host does not need the whole domain registered to survive events it ignores; see above.
If a host must deserialize events itself but should *not* wire the projection classes — a custom
subscriber over `IEventMapperFactory`, say — register only the event types:

```csharp
services.AddDomainEventTypesFromAssemblyContaining<AccountOpened>();
```

This adds only the aggregates' `Apply(SomeEvent)` parameter types to the trusted-type allowlist — no
aggregate types, no handler classes. See the [DI Extensions Cheatsheet](../reference/di-extensions-cheatsheet.md).

## Idempotency is your job

Event bundles are delivered at-least-once, so a projection may see the same event twice — after a
retry or during a replay. Write handlers that converge rather than accumulate:

| Pattern | Safe on redelivery? |
|---|---|
| `store.UpsertAsync(id, absoluteValue)` | Yes — replays write the same value |
| `store.AddAsync(id, delta)` | **No** — a redelivery double-counts unless you guard on `@event.Version` |

The `AddAsync` lines above are the honest trade-off this example makes for brevity. In production,
either derive the absolute value, or record the last applied `Version` per stream and skip anything
already seen.

### Order is per aggregate, and per process

Bundles are not delivered in order across consumers. The projection worker opens several consumers
on its subscription — one per processor unless `Projections:DegreeOfParallelism` says otherwise —
and the broker hands consecutive bundles to different ones. What the worker guarantees is narrower
and enough: **bundles about one aggregate are applied one at a time within a process.** Two bundles
about different aggregates run in parallel; two about the same one queue behind each other. Across
two processes consuming the same subscription there is no such promise.

The lock serialises; it does not order. Rarely, the follow-up fact takes the lock before the fact
that created its entity, finds no row, and has to decide what that means. Say so, and the framework
does the rest:

<!-- stratara-snippet-ignore: narrative fragment - the repository and the event come from the surrounding text -->
```csharp
var entry = await repository.GetAsync(@event.StreamId, cancellationToken)
    ?? throw new PrecedingFactMissingException(@event.StreamId, nameof(EntryProcessingStepStarted));
```

`PrecedingFactMissingException` is the one exception the worker retries: six attempts, from 100 ms
doubling, about three seconds in all, with the aggregate lock **released between attempts** so the
creating fact can land in the gap. Any other exception fails the bundle on the first attempt, as
*A failing projection stops the bundle* requires. If the retries run out, the bundle fails the same
way, and the log names the stream and the event type — at that point the beginning is not late, it
is missing.

A failed bundle is not lost. The transport delivers it again — up to `MessageRetry:MaxDeliveryAttempts`
times (default 3) — and then moves it to the projection subscription's dead-letter destination
(`<subscription>.dead-letter` on RabbitMQ, the subscription's DLQ on Service Bus), logged as `108_110`
and counted on `messaging.dead_lettered`. Fix the cause, return the message, and the read model
catches up; a full replay is the repair of last resort, not the only one. See
[When a handler cannot take a message](outbox-setup-rabbitmq.md#when-a-handler-cannot-take-a-message).

A host that needs every bundle applied in the order the transport delivers it sets
`Projections:DegreeOfParallelism` to `1`. A value that is not a positive number means one consumer
per processor.

### Within one commit, a stream's own order holds

A projection that reads the store in commit order — the Orleans execution model's way — receives the
entries of one save with **each stream's entries in version order**, under either reader. A stream
whose creating fact and its follow-up were committed together, which is the natural shape of
"accepted, and a request raised", therefore arrives beginning first, and the follow-up handler finds
its row.

Entries of *different* streams committed together may be interleaved in any order. Nothing promises
that stream A's facts all precede stream B's, so a projection that joins two aggregates still needs
`PrecedingFactMissingException` for the case where the other stream's beginning has not landed yet.

### The two races, and the one that needs help

Two things happen routinely and are not faults. A row can vanish between your read and your write,
because a cascading delete got there first. And a delete can conflict with a concurrent delete of
the same row — the end state you wanted has simply been reached by someone else.

The first needs no helper. Load the row and return when it is not there:

<!-- stratara-snippet-ignore: narrative fragment - the repository and the event come from the surrounding text -->
```csharp
var tenant = await repository.GetAsync(@event.StreamId, cancellationToken);
if (tenant is null) { return; }
```

The second is the one that is easy to get wrong, so the framework ships it:

<!-- stratara-snippet-ignore: narrative fragment - the repository and the event come from the surrounding text -->
```csharp
await repository.DeleteAsync(@event.StreamId, cancellationToken);

await transaction.SaveChangesIdempotentAsync(
    ct => repository.ExistsAsync(@event.StreamId, ct), cancellationToken);
```

**The probe is the point.** On a conflict the helper asks whether the target is still there. Gone
means a concurrent bundle reached the same end state, and the commit is treated as satisfied. Still
there means a second writer changed a live row — a real conflict, rethrown, and the bundle fails as
it must. Catching `ConcurrencyConflictException` broadly instead would turn "a failing projection
stops the bundle" into a guarantee that holds only where nobody used the helper.

## A deleted tenant's late facts

A projection that removes a tenant's rows when the tenant is deleted — on `TenantDeleted`, or on the
`CustomerTenantsDeleted` cascade — will sooner or later meet a fact about that tenant's data that was
recorded *after* the deletion: work queued before it ran to its end. The row is gone, the handler
throws `PrecedingFactMissingException`, and the framework cannot tell "not applied yet" from "removed
on purpose". Live, the bundle is retried and dead-lettered; in a replay the batch fails on every
attempt, and the read models the replay emptied stay empty.

Declare `IForgetsDeletedTenants` on such a projection. The declaration is a promise: once *either*
deletion fact has been applied, the tenant's data is gone from this projection's read model — removed
by its own handlers, as below, or by anything else. A projection that keeps a tenant's rows after
`TenantDeleted`, the tenant's soft delete, must not declare it.

<!-- stratara-snippet-ignore: narrative fragment - the repository is the consumer's own -->
```csharp
public sealed class KnowledgeEntryProjection(IKnowledgeEntryRepository entries) : IForgetsDeletedTenants
{
    private async Task HandleAsync(IEvent<EntryIndexed> @event, CancellationToken cancellationToken)
    {
        var entry = await entries.FindAsync(@event.StreamId, cancellationToken)
                    ?? throw new PrecedingFactMissingException(@event.StreamId, nameof(EntryIndexed));
        await entries.MarkIndexedAsync(entry, cancellationToken);
    }

    private Task HandleAsync(IEvent<TenantDeleted> @event, CancellationToken cancellationToken) =>
        entries.DeleteForTenantsAsync([@event.StreamId], cancellationToken);

    private Task HandleAsync(CustomerTenantsDeleted @event, CancellationToken cancellationToken) =>
        entries.DeleteForTenantsAsync(@event.TenantIds, cancellationToken);
}
```

The framework then does three things for this projection, and nothing else:

- It hands the projection `TenantDeleted` and `CustomerTenantsDeleted` whether or not it handles them,
  and after applying one records the tenants it deleted — for this projection alone, in the order the
  projection applies facts. What one projection has recorded never depends on how far another has
  got; the partitions of one projection on the Orleans execution model converge through the usual
  retry until each has applied the deletion.
- When the projection throws `PrecedingFactMissingException` for a fact whose owning tenant it has
  recorded, the fact is passed over: treated as applied, not retried, and logged at Information
  (`104_014`) with the projection, the stream, the fact's type and the tenant.
- A replay empties the record of each declaring projection the host registers, just before it empties
  the read models, and leaves another deployment's records in a shared read store alone; rebuilding
  an `IRebuildableProjection` on its own on the Orleans execution model empties its record just before
  its read model. The record is rebuilt from the history, in order.

A fact of a deleted tenant that the projection applies without complaint — a late creation, say —
is applied as usual; if you want those dropped too, inject `IForgottenTenantStore` and ask it. A
missing prerequisite for any tenant not recorded — or one the store could not be asked about — is
retried and fails as before, and a projection that does not declare the interface is not affected at
all.

**The record lives in the read store.** The framework's read context declares the table
`projection_forgotten_tenant`, so upgrading needs a migration of your read context.
`AddNpgsqlReadDbContextFactory<TContext>()` registers the store; for a read context registered another
way, call `AddStrataraForgottenTenants<TReadContext>()`. A declaring projection in a host without the
store fails on the first fact it is handed, naming both; a host without a declaring projection never
touches the table. Deletions applied before the upgrade are not in the record until a replay records
them from the history — or, on the Orleans execution model, a rebuild of an `IRebuildableProjection`.

**Discovery trusts the deletion facts for you.** `AddProjectionsFromAssemblyContaining<T>()` adds
`TenantDeleted` and `CustomerTenantsDeleted` to the trusted types for a declaring projection; a
projection registered by hand needs `AddTrustedType<TenantDeleted>()` and
`AddTrustedType<CustomerTenantsDeleted>()`. A host that replaces `IProjectionHandler` gives the
behaviour up with the rest of the default handler.

## What the framework does not do

On the bus path there is **no checkpoint store**. Projections are driven push-wise off the event bus;
Stratara does not track how far each projection has progressed, so there is no consumer-lag metric and
no resume-from-sequence. The observability you get is throughput and latency
(`projection.events.processed`, `projection.bundle.duration`). Under the
[Orleans execution model](../concepts/orleans-execution-model.md) the same projection reads the store
from a checkpoint per projection and partition, kept in the read store by
`AddStrataraProjectionCheckpoints<TReadContext>()`, and its lag is measured; the checkpoint is keyed by
the projection's name. Replay of the historical stream is coordinated separately, via the
`IProjectionReplayState` in `Stratara.Outbox.RabbitMQ`.

**Where that state lives depends on one registration.** With a Redis connection registered
(`builder.AddCaching()`, connection string `redis`) the replay marking, its progress and the
replay-request channel are shared by every host on that connection: a replay requested in the
projection worker suppresses publication in the API host too. Without one, the state is held in
process — a single host needs no Redis at all — and a replay requested in one host is neither seen
by nor suppresses anything in another. A host that falls back says so once at start-up with warning
`104_012`. A deployment of several hosts that needs a replay to suppress publication in all of them
registers the shared connection; the order of that call and the composites does not matter.

## Replay is destructive, and it is all-or-nothing

A replay is not a repair tool you reach for casually. Three properties, in the order they will
surprise you:

**It empties before it rebuilds.** A replay marks itself active, truncates *every registered read
model*, then replays the whole stream from the beginning in batches. The truncation is what makes it
a rebuild rather than a second application of events on top of state that already reflects them —
but it means the read side is empty from the instant the replay starts, and stays that way until the
rebuild passes each row again.

**There is no per-projection scope.** You cannot replay one projection. Every read model registered
in the host is emptied, including the ones that were fine.

**It runs on request, with no confirmation step.** The worker does not start one at host start-up —
it subscribes and waits. But when a request arrives it begins immediately. There is no dry run, no
"are you sure", and no built-in guard on who may ask.

**A batch that fails is retried before the replay gives up.** Each batch — reading it from the
event store and applying it — runs under the `ResilienceNames.ProjectionReplayBatch` policy: five
attempts in all, exponential backoff from one second with jitter between them — about fifteen
seconds of waiting plus jitter before the last one — any exception except cancellation. A read-store timeout or a dropped connection mid-rebuild is retried;
each attempt after a failure logs `104_011` so you can see the replay struggling rather than merely
slow. A retried batch is applied again **from its first entry**, in a fresh scope — which is why the
converge-not-accumulate rule above is not optional. A failure that persists through every attempt
ends the replay exactly as an unretried one would.

**Each stream is replayed in version order.** A save does not number the entries it writes in
version order — the database hands out sequence numbers in the order the provider chose to insert
the rows — so the sequence number alone can put a stream's third fact before its first. The replay
reads each batch with every stream's entries in version order and the streams interleaved as their
sequence numbers interleave them, so a projection that stops on a missing preceding fact does not
stop on a sound stream. A batch that would end between two versions of one stream is extended
until it does not, so a batch can hold more entries than `Projections:BatchSize`. A store written
before this guarantee needs no migration; the order comes from reading, not from the rows.

And when it ends, it marks itself inactive **whether it succeeded or not**. A replay that dies
half-way leaves you with partially rebuilt read models and no active flag saying so — only the
failure message described under [Watch a replay](#watch-a-replay). Treat a failed replay
as "run it again", not as "it stopped safely".

**A replay is a maintenance operation.** Run it in a window, after a backup of the read store. The
retry above covers a failure that passes; it does not make a deterministic one survivable, and the
framework does not keep the previous views for you. If a replay fails and does not complete on a
second or third attempt, the backup is the fallback while you look for the cause — that procedure
belongs to your operations, not to the framework.

**A host that is killed does not get to mark anything.** Failing is an ending; being killed is not.
A `SIGKILL`, a container stop, an out-of-memory kill or a reboot leaves the replay with no chance to
clear its own marking — and while the marking stands, publication stays suppressed for the whole
host: commands are recorded instead of sent, the caller gets an identifier and a success response for
a command that will never run, and the outbox does not drain.

So the marking is held on a lease that the replay renews each time it reports progress. Nobody
renewing it means nobody is replaying, and it lapses on its own. Set the lease longer than your
slowest stretch between two progress reports — the slowest batch, and the read-model truncation that
precedes the first report:

```csharp
builder.Services.Configure<ProjectionReplayOptions>(
    o => o.LeaseSeconds = 600);   // default 300
```

Or set it in the `ProjectionReplay` configuration section (`ProjectionReplayOptions.SectionName`).
`AddProjectionReplayState()` — which `AddOutboxDispatcher()` and the worker composites call — reads
the section from the host's configuration as it stands, with no code; a value configured in code after
that call takes precedence:

```jsonc
{
  "ProjectionReplay": {
    "LeaseSeconds": 600
  }
}
```

A lease of zero or less is refused when the host starts, with an `OptionsValidationException`
naming `ProjectionReplay:LeaseSeconds`. Err long. Too long only delays the clearing of a marking whose replay already died; too short lets
the marking lapse while the replay is still running, which resumes suppressed publication against
half-rebuilt read models and tells nobody.

One thing a version bump does not do for you: a marking that is *already* stuck from before you
adopted the lease was written without an expiry and does not gain one. Clear it once — an explicit
deactivation, or let the next replay's own completion clear it.

## Watch a replay

A replay publishes how far it has got, and records why it stopped when it fails, so an operator can
tell "still running" from "stopped part way". `IProjectionReplayState.GetProgress()` returns a
`ReplayProgress`:

| Member | While running | After a failure | After success, or before any replay |
|---|---|---|---|
| `IsActive` | `true` | `false` | `false` |
| `ProcessedEvents` / `TotalEvents` | events applied so far / events to replay | `0` / `0` | `0` / `0` |
| `Percentage` | `0`–`100`, derived from the two counts | `0` | `0` |
| `ErrorMessage` | `null` | the failure's message | `null` |

The total is published once the read models are truncated, so a replay that is still truncating
reports `0` of `0`. A total of zero yields a percentage of `0`, never a division failure. A failure
message longer than 500 characters is truncated rather than stored whole, and it stays readable until
the next replay starts. A replay interrupted by **host shutdown** is not recorded as a failure —
shutdown is not a replay error, so it leaves no message behind.

Reading and requesting a replay from an admin endpoint takes two lines; the guard on them is yours,
because the framework has none:

```csharp
app.MapGet("/admin/projections/replay", (IProjectionReplayState replay) => replay.GetProgress())
    .RequireAuthorization("PlatformAdmin");

app.MapPost("/admin/projections/replay", (IProjectionReplayState replay) => replay.RequestReplay())
    .RequireAuthorization("PlatformAdmin");
```

What the endpoint sees follows the registration described under
[What the framework does not do](#what-the-framework-does-not-do): with the shared Redis connection
every host reads the same progress, without it each host reads only its own.

## A replayed event runs under the session that recorded it

While replaying, each event is applied under the **session context recorded with it** — its owning
tenant and user, its actor, its correlation and causation ids — not under the replaying host's own.
A handler that reads `ISessionContextProvider.Current` during a replay sees the session of the
request that produced the event, event by event, even when one batch holds events from many tenants.

That is what keeps a rebuild faithful: without it, every rebuilt row would be attributed to whichever
session happened to be ambient, and tenant-scoped writes would land in the wrong tenant. Write
handlers that take the tenant from the event or from the session, and a replay puts each row back
where it was.

## See also

- **[Sample 2 — Event Sourced](../samples/02-event-sourced.md)** — an aggregate and its projection end to end.
- **[Write a Saga](write-a-saga.md)** — the sibling pattern that reacts to events by issuing commands.
