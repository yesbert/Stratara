# observability Specification

## Purpose
Make a running Stratara host legible from outside — which work it did, how long it took, what
failed and whether it is healthy — through names stable enough that a dashboard built against one
version keeps working against the next.

## Requirements

### Requirement: Instrument names are a stable published contract

The activity source name, the meter name, every instrument name and every tag name the framework
emits SHALL be treated as published API. Changing one SHALL be a breaking change, because a
consumer's dashboards, alerts and log queries reference them by name and nothing in a consumer's
build would detect the rename.

The framework SHALL publish these names as constants, so a consumer can reference them rather than
duplicating string literals.

#### Scenario: A consumer builds a dashboard on an instrument

- **WHEN** a consumer queries an instrument or a tag by the name the framework publishes
- **THEN** that name continues to identify the same measurement across versions within a major
  version

#### Scenario: A consumer needs to reference a name in code

- **WHEN** a consumer needs the activity source name, the meter name, an outcome value or a tag name
- **THEN** it is available as a published constant rather than only as a literal in emitted data

### Requirement: All framework telemetry originates from one source and one meter

Every trace the framework emits SHALL come from a single named activity source, and every metric
from a single named meter, so that a consumer subscribes to Stratara telemetry with one
registration rather than tracking a list that grows per release.

#### Scenario: A consumer subscribes to framework telemetry

- **WHEN** a consumer registers the framework's activity source and meter with its telemetry
  pipeline
- **THEN** it receives all traces and metrics the framework emits, including from packages it adds
  later

### Requirement: The framework measures throughput and latency across the event pipeline

The framework SHALL emit counters for events appended, outbox entries published, projection events
processed and saga events processed; histograms for command duration, projection bundle duration
and saga bundle duration; a counter for optimistic-concurrency conflicts; and a gauge for sagas
currently in flight. Measurements SHALL be dimensioned by the aggregate type, event type, request
type, outcome and outbox kind they concern.

#### Scenario: An operator asks how much work the host is doing

- **WHEN** an operator queries the framework's instruments
- **THEN** throughput and latency are available for the command path, the event store, the outbox,
  projections and sagas, broken down by outcome

#### Scenario: An operator asks how far behind a projection is

- **WHEN** an operator looks for consumer lag — how far a projection or saga trails the event stream
- **THEN** the framework does not answer it. There is no checkpoint store for projections or sagas,
  so lag is not measurable from these instruments, and no instrument implies otherwise

### Requirement: Log event ids follow a partitioned schema that reserves a consumer range

Every log message the framework emits SHALL carry an event id from a published schema, partitioned
by subsystem within a reserved range. Ids outside that range SHALL be left to consumer applications,
so that a consumer's own ids can never collide with the framework's.

#### Scenario: An operator filters logs by event id

- **WHEN** an operator filters on a framework event id
- **THEN** it identifies one message shape from one subsystem, and the subsystem is derivable from
  the id's band

#### Scenario: A consumer assigns its own event ids

- **WHEN** a consumer application assigns event ids to its own log messages
- **THEN** it can do so without consulting the framework's schema, because the framework's range and
  the consumer's range do not overlap

### Requirement: Sensitive request headers are redacted from traces

Where the framework configures HTTP tracing, it SHALL replace the values of authorization, cookie,
proxy-authorization and set-cookie header tags with a redaction marker before the span is exported.

A credential captured in a trace is exported to wherever traces go and retained for as long as
traces are retained, which is usually longer and less protected than the credential's own lifetime.

#### Scenario: A request carries an authorization header

- **WHEN** an HTTP request or response carrying an authorization, cookie, proxy-authorization or
  set-cookie header is traced
- **THEN** the corresponding span tag carries a redaction marker rather than the header's value

#### Scenario: A request carries no such header

- **WHEN** none of those headers is present
- **THEN** no redaction tag is added — redaction replaces a value, it does not invent one

### Requirement: Protected field values never appear in log messages

Log messages SHALL NOT include the values of fields marked for encryption, even at verbose levels.
Where a message describes a change to such fields, it SHALL name the fields without their values.

#### Scenario: A change set containing protected fields is logged

- **WHEN** a change set covering fields marked for encryption is logged at debug level
- **THEN** the message names the changed fields and does not include their values

#### Scenario: The verbose level is not enabled

- **WHEN** the level a message would be written at is not enabled
- **THEN** the framework does not do the work of assembling that message

### Requirement: Health and liveness are separate endpoints

A host SHALL be able to expose a readiness endpoint reporting every registered check, and a
liveness endpoint reporting only checks tagged as liveness probes, so that a dependency being
unavailable does not cause an orchestrator to restart an otherwise healthy process.

#### Scenario: An orchestrator probes liveness

- **WHEN** the liveness endpoint is probed
- **THEN** only liveness-tagged checks are evaluated, and a failing dependency check does not make
  the process appear dead

#### Scenario: A load balancer probes readiness

- **WHEN** the health endpoint is probed
- **THEN** every registered check is evaluated

### Requirement: The health endpoint may require authorization without affecting liveness

A host SHALL be able to require authorization on the readiness endpoint, and doing so SHALL NOT
affect the liveness endpoint, which an orchestrator probes without credentials.

#### Scenario: Authorization is not required

- **WHEN** the health endpoint is mapped without requiring authorization and an unauthenticated
  caller probes it
- **THEN** it responds successfully

#### Scenario: Authorization is required

- **WHEN** the health endpoint is mapped requiring authorization and an unauthenticated caller
  probes it
- **THEN** the health endpoint refuses the caller
- **AND** the liveness endpoint still responds successfully to the same unauthenticated caller

### Requirement: Health probes for the event store and the outbox are opt-in

The framework SHALL offer a readiness check reporting whether the event store is reachable, and one
reporting the outbox backlog against caller-supplied thresholds. Both SHALL be opt-in, and both
SHALL report a probe failure as unhealthy rather than propagating it.

#### Scenario: The event store is reachable

- **WHEN** the event-store check runs and the store answers
- **THEN** the check reports healthy

#### Scenario: The probe itself fails

- **WHEN** either check cannot complete — the store is unreachable or the backlog query fails
- **THEN** the check reports unhealthy rather than throwing into the health pipeline

#### Scenario: The outbox backlog crosses a threshold

- **WHEN** the outbox backlog check runs with thresholds supplied and the pending count reaches the
  degraded or the unhealthy threshold
- **THEN** the check reports degraded or unhealthy accordingly, and reports the pending count either
  way

#### Scenario: No thresholds are supplied

- **WHEN** the outbox backlog check runs with no thresholds
- **THEN** it reports healthy and still reports the pending count — it observes without judging

### Requirement: Health endpoints are excluded from tracing

Requests to the health and liveness endpoints SHALL NOT be traced, so that orchestrator probes do
not dominate a host's trace volume.

#### Scenario: An orchestrator probes repeatedly

- **WHEN** the health or liveness endpoint is probed
- **THEN** no trace span is recorded for that request

### Requirement: Telemetry export is configured by environment, not by code

Where an OpenTelemetry endpoint is configured in the environment, the framework SHALL export traces,
metrics and logs to it. Where none is configured, the framework SHALL configure telemetry without an
exporter rather than failing.

#### Scenario: An export endpoint is configured

- **WHEN** an OpenTelemetry endpoint is present in configuration
- **THEN** traces, metrics and logs are exported to it, carrying the configured service name

#### Scenario: No export endpoint is configured

- **WHEN** no OpenTelemetry endpoint is configured
- **THEN** telemetry is still collected in-process and no exporter is registered — a host runs
  locally without an observability backend
