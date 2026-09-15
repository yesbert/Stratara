## MODIFIED Requirements

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

- **WHEN** a consumer needs the activity source name, the meter name, an instrument name, an outcome
  value or a tag name
- **THEN** it is available as a published constant rather than only as a literal in emitted data

### Requirement: The framework measures throughput and latency across the event pipeline

The framework SHALL emit counters for events appended, outbox entries published, projection events
processed and saga events processed; histograms for command duration, projection bundle duration
and saga bundle duration; a counter for optimistic-concurrency conflicts; and a gauge for sagas
currently in flight. Measurements SHALL be dimensioned by the aggregate type, event type, request
type, outcome and outbox kind they concern; a gauge of work in flight SHALL NOT be dimensioned by
outcome, because that work has none yet.

A tag that names an event type or an aggregate type SHALL carry the same value form on every
instrument that emits it — the type's simple name — so that a consumer can join the write side's
series with the projection and saga series on that value.

#### Scenario: An operator asks how much work the host is doing

- **WHEN** an operator queries the framework's instruments
- **THEN** throughput and latency are available for the command path, the event store, the outbox,
  projections and sagas, broken down by outcome where the operation has one

#### Scenario: An operator joins write and read throughput for one event type

- **WHEN** an operator filters the events-appended series and the projection and saga
  events-processed series by the same event type
- **THEN** all of them carry the same tag value for that event type, and the filter matches in each

#### Scenario: An operator asks how far behind a projection is

- **WHEN** an operator looks for consumer lag — how far a projection or saga trails the event stream
- **THEN** the framework does not answer it. There is no checkpoint store for projections or sagas,
  so lag is not measurable from these instruments, and no instrument implies otherwise
