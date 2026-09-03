## MODIFIED Requirements

### Requirement: Every dispatch is traced

Dispatching SHALL open a trace span named for the request type, so that a request's path through the
pipeline and its handler is visible without any handler emitting telemetry itself.

Tracing SHALL NOT be a precondition for dispatching. A host that registers the mediator and supplies
no tracing of its own SHALL dispatch requests; in that case the framework SHALL emit the dispatch
spans from its own single activity source, so that a host which subscribes to framework telemetry
receives them without further registration, and a host which subscribes to nothing pays for nothing.
A host that supplies its own tracer SHALL keep it — the framework's fallback is used only in its
absence.

#### Scenario: A request is dispatched while tracing is enabled

- **WHEN** any request is dispatched
- **THEN** a span identifying the request type covers the pipeline and the handler

#### Scenario: A host registers the mediator and nothing else

- **WHEN** a host registers the mediator without registering any tracing infrastructure and
  dispatches a request
- **THEN** the request reaches its handler and the result is returned; nothing fails for want of a
  tracer

#### Scenario: A host without a tracer of its own subscribes to framework telemetry

- **WHEN** a host registers the mediator, supplies no tracer of its own, and subscribes to the
  framework's activity source
- **THEN** each dispatch is visible to that subscription as a span identifying the request type

#### Scenario: A host supplies its own tracer

- **WHEN** a host registers its own tracer before or after registering the mediator
- **THEN** dispatch spans are emitted through that tracer, exactly as before the framework offered a
  fallback
