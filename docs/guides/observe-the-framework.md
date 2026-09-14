---
title: "Observe the Framework"
description: "The one activity source and one meter every Stratara package reports through, every instrument with its tags, what never reaches a trace or a log, and the health endpoints a host exposes."
---

# Observe the Framework

> **Derived page.** The behaviour described here is specified by the `observability` capability
> under `openspec/specs/`. That specification is the source; this page explains and illustrates it.
> Where the two disagree, the specification is right and this page is a bug.

A running Stratara host tells you which work it did, how long that took, what failed and whether it
is healthy. This page covers where that telemetry comes from, the names you can build on, every
instrument the framework records, what the framework keeps out of traces and logs, and the health
endpoints.

## One source, one meter

**Every trace the framework emits comes from one activity source, and every metric from one meter.**
You subscribe to Stratara telemetry with one registration each. That registration also covers
packages you add later, so you never have to keep a list of sources up to date.

| What | Name | Constant |
|---|---|---|
| Activity source | `Stratara.Application` | `ApplicationDiagnostics.Activity.SourceName` |
| Meter | `Stratara.Service` | `ApplicationDiagnostics.Metrics.MeterName` |

Both live in `Stratara.Diagnostics`. The source and the meter themselves are published too, as
`ApplicationDiagnostics.Activity.Source` and `ApplicationDiagnostics.Metrics.Meter`.

A host built on `Stratara.ServiceDefaults` already subscribes to both:

```csharp
builder.ConfigureOpenTelemetry();
```

If you build your own OpenTelemetry pipeline, register the two names from their constants:

```csharp
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Stratara.Diagnostics;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(ApplicationDiagnostics.Activity.SourceName))
    .WithMetrics(metrics => metrics.AddMeter(ApplicationDiagnostics.Metrics.MeterName));
```

The mediator traces every dispatch. If the host has registered a tracer, the mediator uses it.
Otherwise it emits the dispatch spans from this same source, so you don't need a separate
registration for them.

## Names are a published contract

**The activity source name, the meter name, every instrument name and every tag name are published
API.** Your dashboards, alerts and log queries refer to them by name, and nothing in your build would
notice if one were renamed. So a rename is a breaking change and waits for a major version. Within a
major version, a name you query keeps measuring the same thing.

You can reference the names in code instead of copying string literals:

- **Source and meter:** `ApplicationDiagnostics.Activity.SourceName`,
  `ApplicationDiagnostics.Metrics.MeterName`.
- **Metric tag names:** the constants on `ApplicationDiagnostics.MetricTags`.
- **Tag values:** `ApplicationDiagnostics.Outcomes` (`success`, `failure`) and
  `ApplicationDiagnostics.OutboxKinds` (`command`, `event`). The dead-letter reasons are
  `MessageRetryPolicy.ConflictReason` (`conflict`) and `MessageRetryPolicy.FailureReason` (`failure`).
- **Trace tag names for the session:** `ApplicationDiagnostics.CorrelationIdTagName`
  (`correlation.id`), `ApplicationDiagnostics.CausationIdTagName` (`causation.id`),
  `ApplicationDiagnostics.TenantIdTagName` (`tenant.id`) and `ApplicationDiagnostics.UserIdTagName`
  (`user.id`).
- **Instruments:** each one is a public static field on `ApplicationDiagnostics.Metrics`, and its
  name is that field's `Name`. For example, `ApplicationDiagnostics.Metrics.SagasInFlight.Name` is
  `saga.inflight`.

## Every instrument

The framework measures throughput and latency across the event pipeline: the command path, the event
store, the outbox, projections and sagas. Each measurement is broken down by outcome where the
operation can fail. All instruments belong to the `Stratara.Service` meter.

| Instrument | Kind | Unit | Tags | What it measures | Field on `ApplicationDiagnostics.Metrics` |
|---|---|---|---|---|---|
| `event_source.events.appended` | Counter (`long`) | `{event}` | `event.type`, `aggregate.type` | Domain events appended to an event stream | `EventsAppended` |
| `event_source.append.conflicts` | Counter (`long`) | `{conflict}` | `aggregate.type`, `bucket.id` | Optimistic-concurrency conflicts detected when appending events to a stream | `EventSourceAppendConflicts` |
| `outbox.published` | Counter (`long`) | `{entry}` | `outbox.kind` | Outbox entries successfully published | `OutboxEntriesPublished` |
| `command.duration` | Histogram (`double`) | `ms` | `request.type`, `outcome` | Latency of commands dispatched through the outbox worker | `CommandDuration` |
| `projection.events.processed` | Counter (`long`) | `{event}` | `event.type`, `outcome` | Events dispatched to projection handlers | `ProjectionEventsProcessed` |
| `projection.bundle.duration` | Histogram (`double`) | `ms` | `outcome` | Time to process one projection event bundle | `ProjectionBundleDuration` |
| `saga.events.processed` | Counter (`long`) | `{event}` | `event.type`, `outcome` | Events dispatched to saga handlers | `SagaEventsProcessed` |
| `saga.bundle.duration` | Histogram (`double`) | `ms` | `outcome` | Time to process one saga event bundle | `SagaBundleDuration` |
| `saga.inflight` | UpDownCounter (`long`) | `{bundle}` | none | Saga event bundles being processed right now, across all saga subscriptions | `SagasInFlight` |
| `messaging.dead_lettered` | Counter (`long`) | `{message}` | `messaging.topic`, `messaging.subscription`, `reason` | Messages moved to a dead-letter destination after they used up their redeliveries | `MessagesDeadLettered` |

The tags, with the constant on `ApplicationDiagnostics.MetricTags` that holds each name:

| Tag | Constant | Value |
|---|---|---|
| `aggregate.type` | `AggregateType` | The aggregate type the append concerned |
| `bucket.id` | `BucketId` | The bucket-lock bucket index of the stream that conflicted |
| `event.type` | `EventType` | The domain-event type |
| `request.type` | `RequestType` | The simple name of the command type |
| `outcome` | `Outcome` | `success` or `failure` |
| `outbox.kind` | `OutboxKind` | `command` or `event` |
| `messaging.topic` | `Topic` | The topic the message was published to |
| `messaging.subscription` | `Subscription` | The subscription it was consumed under |
| `reason` | `Reason` | `conflict` or `failure` |

What to know when you read them:

- **The concurrency-conflict counter** goes up when two writers append to the same stream at the same
  version and one of them loses. The losing append throws `ConcurrencyException`. A steady trickle
  is normal under contention. If it keeps rising for one `aggregate.type`, that aggregate is a hot
  spot.
- **`command.duration` is outbox command latency, not end-to-end command latency.** It records
  commands that the command worker picks up from the outbox. A command you dispatch in-process through
  the mediator records nothing here. A host that only dispatches directly sees an empty histogram,
  and a host that does both sees only its outbox traffic.
- **`outbox.published` and `command.duration`** are recorded by the outbox and command workers in
  `Stratara.Outbox.RabbitMQ`. **`messaging.dead_lettered`** is recorded by both the RabbitMQ and the
  Azure Service Bus message bus.
- **The saga instruments** measure bundles, not saga instances. `saga.events.processed` and
  `saga.bundle.duration` are recorded when a bundle finishes. Every event in the bundle is counted
  with the bundle's outcome, and that outcome is `failure` if the bundle's processing threw.
  `saga.inflight` goes up when a bundle starts and down when it finishes, whatever the outcome. If it
  keeps rising, the saga lane cannot keep up with the rate events arrive. The projection instruments
  work the same way, apart from the in-flight gauge, which sagas alone have.
- **Consumer lag is not measured.** No instrument tells you how far a projection or a saga trails the
  event stream, and none should be read as if it did. Projections and sagas have no checkpoint store,
  so lag can't be measured from these instruments.

In a test, you can listen to one instrument by its published name:

```csharp
using System.Diagnostics.Metrics;
using Stratara.Diagnostics;

var conflicts = 0L;
var listener = new MeterListener
{
    InstrumentPublished = (instrument, meterListener) =>
    {
        if (instrument.Meter.Name == ApplicationDiagnostics.Metrics.MeterName
            && instrument.Name == ApplicationDiagnostics.Metrics.EventSourceAppendConflicts.Name)
        {
            meterListener.EnableMeasurementEvents(instrument);
        }
    },
};
listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref conflicts, value));
listener.Start();
```

## Log event ids

Every log message the framework writes has an event id from a published schema. The ids are
partitioned by subsystem inside a reserved framework range, so the band an id falls in tells you
which subsystem wrote it. Ids outside that range belong to your application, so your own event ids
can never collide with the framework's. You don't need to consult the schema to pick them. The
ranges, the subsystem bands and the rules for writing your own messages are in the
[LogEvents schema](../reference/log-events-schema.md).

## Credentials never reach a trace

**Where the framework configures HTTP tracing, it replaces the values of the authorization, cookie,
proxy-authorization and set-cookie header tags with a redaction marker before the span is exported.**
A credential in a trace goes wherever your traces go and stays for as long as they are kept. That is
usually longer, and less protected, than the credential's own lifetime.

This applies to outgoing HTTP calls traced by `ConfigureOpenTelemetry()` and to incoming requests
traced by `ConfigureAspNetOpenTelemetry()` from `Stratara.ServiceDefaults.AspNetCore`. The tags it
replaces are `http.request.header.authorization`, `http.request.header.cookie`,
`http.request.header.proxy_authorization` and `http.response.header.set_cookie`. Each gets the value
`REDACTED`.

Redaction replaces a value. It never adds one. If a request has none of these headers, no tag is
added. OpenTelemetry records no headers at all unless you opt into header capture, so for most hosts
this is a second line of defence. It matters from the day somebody turns header capture on to debug
a problem.

## Protected field values never appear in a log message

**Log messages never include the values of fields marked for encryption, at any level, including
debug.** When a message describes a change to such fields, it names the fields and leaves out their
values.

For example, when an update handler computes a change set, the framework logs it at debug level as
the aggregate id, the number of changed fields and their property names. The old and new values stay
out of the message. If you mark a property `[EncryptData]`, its value goes only to the event store,
encrypted, and never through your log pipeline.

When debug is not enabled, the framework skips building that message: the list of names is joined
only when the message is actually written. You don't pay for verbose logging you have turned off.

The same rule applies to your own messages. See
[Encrypt sensitive data](encrypt-data-setup.md) for marking fields.

## Health and liveness

`Stratara.ServiceDefaults.AspNetCore` gives a host two endpoints:

- **`/health`** (readiness) runs every registered check.
- **`/alive`** (liveness) runs only the checks tagged `live`.

The split lets an orchestrator restart a process that is actually dead, without restarting a healthy
process just because a dependency is down. A database outage makes `/health` report unhealthy while
`/alive` keeps answering.

**The readiness endpoint can require authorization without affecting liveness.** The full health
report names every dependency you registered, which tells an attacker what your deployment looks
like. Pass `requireAuthorizationOnHealth: true` and an unauthenticated caller is refused on
`/health`. `/alive` still answers the same caller, because orchestrators probe liveness without
credentials. By default, neither endpoint requires authorization.

**Two checks are available, and you add them yourself.** The first reports whether the event store is
reachable. The second reports the outbox backlog against thresholds you supply. They come from
`Stratara.EventSourcing.EntityFrameworkCore` and need the write store registered.

- **Event store:** healthy when the store answers.
- **Outbox backlog:** reports degraded or unhealthy when the number of pending entries reaches the
  threshold you set for that status. It always reports the pending count in the check's data under
  `pending`. With no thresholds it reports healthy and still reports the count, so you can watch the
  backlog without the check ever failing.
- **Either check fails to run** (the store can't be reached, or the backlog query throws): the check
  reports unhealthy instead of throwing into the health pipeline.

Both are registered with the tag `ready` unless you pass tags of your own. Their default registration
names are `eventstore` and `outbox`, published as `StrataraHealthCheckExtensions.EventStoreCheckName`
and `StrataraHealthCheckExtensions.OutboxCheckName`.

```csharp
builder.ConfigureAspNetOpenTelemetry();
builder.AddDefaultHealthChecks();
builder.Services.AddHealthChecks()
    .AddEventStoreHealthCheck()
    .AddOutboxHealthCheck(degradedThreshold: 1_000, unhealthyThreshold: 10_000);

var app = builder.Build();
app.MapDefaultEndpoints(requireAuthorizationOnHealth: true);
```

`AddDefaultHealthChecks()` registers a `self` check tagged `live`, so `/alive` has something to
report.

**Probes are not traced.** `ConfigureAspNetOpenTelemetry()` records no span for requests to `/health`
or `/alive`, so an orchestrator probing every few seconds does not fill your trace volume.

## Export is configured by the environment

**If an OpenTelemetry endpoint is configured, the framework exports traces, metrics and logs to it,
tagged with the configured service name. If none is configured, telemetry is still collected
in-process and no exporter is registered.** Startup never fails for lack of a backend, so a host
runs locally without one.

`ConfigureOpenTelemetry()` reads `OTEL_EXPORTER_OTLP_ENDPOINT`. When the endpoint is set and you have
not set `OTEL_EXPORTER_OTLP_TIMEOUT`, the export timeout defaults to 5000 ms instead of
OpenTelemetry's 10 000 ms. That keeps shutdown quick when the collector can't be reached.

## See also

- [LogEvents schema](../reference/log-events-schema.md): the event-id ranges and subsystem bands
- [Write a saga](write-a-saga.md): the saga worker these instruments measure
- [Write a projection](write-a-projection.md): the projection worker these instruments measure
- [DI extensions cheatsheet](../reference/di-extensions-cheatsheet.md): the observability and
  health-check registrations at a glance
