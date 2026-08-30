> **Status:** approved

# Ship the idempotent-projection helpers the framework already writes by hand

## Why

`projections` specifies that a failing projection handler stops the bundle, and the reasoning is
deliberate: a projection that silently skipped a failed event would leave a read model permanently
inconsistent with the stream, detectable by nothing.

That guarantee has a necessary other half — how a projection avoids failing on a *benign* race — and
the framework does not ship it. It writes it by hand instead. `TenantProjection`, the framework's
own shipped projection, carries the same two patterns five times in one file:

```csharp
var tenant = await repository.GetAsync(@event.StreamId, cancellationToken);
if (tenant is null) { return; }          // the row is gone: nothing to update, not an error
…
catch (ConcurrencyConflictException) { } // the row is already deleted: the end state is reached
```

Its own test explains why the second one is not optional:

> *"re-throwing would abort the bundle and trigger a RabbitMQ requeue that does not recover
> (sibling cascades are already committed)"*

So the framework needed this five times, understood exactly why, and left every consumer to
rediscover it — including the part that is easy to get wrong.

The backlog recorded this as **T2-6** on the strength of one consumer having built a
`ProjectionHelper`. The stronger evidence is internal.

## What Changes

A helper for the two patterns, and a requirement stating what it guarantees — in particular the
distinction that matters and that a naive implementation gets wrong: a conflict on a row that is
**gone** is a satisfied intent, a conflict on a row that still **exists** is a real conflict and must
still stop the bundle.

## Non-goals

Not a replay helper, not a checkpoint store, not a general retry. Those are separate questions and
one of them (`PR-2`) was deliberately dropped during the migration.

## Sequencing

Behind the three security changes and `compose-erasure-sweeps`. This is a quality-of-life
improvement; those are not.
