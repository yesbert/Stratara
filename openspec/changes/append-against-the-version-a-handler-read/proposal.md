# Append against the version a handler read

> **Status:** approved

## Why

A command handler reads a stream, decides on what it read, and appends. The store numbers the new fact
after whatever the stream holds *when the handler appends*, not after what the handler read, so a write
that lands between the read and the append is not a conflict: the new fact is simply recorded after it.
A consumer reported it under load on 2026-10-09 (NextPA finding F-021): a processing stage re-read an
entry, saw it alive, and recorded two follow-up facts after another handler had removed the entry for
good. A fact recorded after a stream's end then stops every later projection replay. The consumer had
already added a re-read before each append; the window between that check and the commit is hundreds of
milliseconds under load, and the bucket lock does not close it because the two handlers are not
serialised against each other. There is no way to say "append only if the stream is still where I read
it".

## What Changes

The consumer-visible effect: a handler can make its append conditional on the stream not having moved
since it read it, and a save that would break that condition fails with the `ConcurrencyException` it
already knows how to retry. Nothing changes for a host that does not ask for it.

- **Explicit form.** `IEventSource.AppendAtVersionAsync<TAggregate>(streamId, expectedVersion, event)`
  and `AppendRangeAtVersionAsync<TAggregate>(streamId, expectedVersion, events)` stage events after
  `expectedVersion`. If another writer has moved the stream past it, `SaveChangesAsync` throws
  `ConcurrencyException` and records nothing. A version above the stream's head is refused at the append,
  because it would leave a gap and can only be a caller's mistake.
- **Implicit form, opt-in.** New `EventSourcingOptions.AppendAgainstAggregatedVersion` (section
  `EventSourcing`, default `false`). When it is on, an append to a stream the handler has rebuilt through
  `IAggregationService.AggregateAsync` in the same scope is staged against the version that read saw —
  every handler that reads before it writes gets the protection without a code change. A read bounded by
  `toVersion` does not set the expectation; a read of a stream that does not exist expects it to stay
  absent.
- On the bus and on the Orleans execution model the conflict is redelivered as today, so the handler runs
  again, reads the stream's end, and decides on that.

Not changed: an `AppendAsync` without the option keeps today's behaviour, the conflict's type, message,
metric and bounds, and the store schema.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `event-sourcing-store`: a new requirement, *An append can be made conditional on the version the caller
  read*.

## Impact

- `src/Stratara.Abstractions/Abstractions/EventSourcing/IEventSource.cs` — the two members, with default
  implementations that throw `NotSupportedException`, so an implementer outside the framework still
  compiles.
- `src/Stratara.Infrastructure/EventSourcing/EventSource.cs` — staging against an expected version; the
  remembered read version consumed in `AppendRangeCoreAsync` and cleared in `ClearBatchState`.
- `src/Stratara.Infrastructure/EventSourcing/AggregationService.cs` — records the head version an
  unbounded read saw.
- `src/Stratara.Infrastructure/EventSourcing/` — a new internal scoped record of read versions shared by
  the two.
- `src/Stratara.Shared/EventSourcing/EventSourcingOptions.cs` — `AppendAgainstAggregatedVersion`; the type
  stops being a marker.
- `src/Stratara.Infrastructure/DependencyInjection/EventSourcingServiceCollectionExtensions.cs` —
  `AddEventSourcing()` binds the `EventSourcing` section and registers the record.
- `docs/guides/write-a-command-handler.md` (*When two writers race*), `docs/reference/di-extensions-cheatsheet.md`,
  `CHANGELOG.md`.
- No package, dependency or tier changes. Additive: a patch release.
