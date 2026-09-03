## ADDED Requirements

### Requirement: Replay coordination does not require shared infrastructure in a single-process host

The framework SHALL hold the replay coordination state — the active marking, the progress counters,
the failure message and the replay-request channel — in process where the host registers no shared
coordination store, and in that shared coordination store where it registers one. A host SHALL start and dispatch
in either case; the absence of a shared coordination store SHALL NOT be a start-up or first-dispatch
failure.

A host that holds the state in process SHALL record, once at start-up, that replay coordination is
confined to that process, so that an operator running several hosts learns it from the log rather
than from a side effect that was not suppressed.

#### Scenario: A host registers no shared coordination store

- **WHEN** a host composes any role that carries a dispatcher and registers no shared coordination
  store
- **THEN** the host starts, commands and bundles are dispatched, and a replay requested in that host
  runs in that host with its progress readable there

#### Scenario: A host registers a shared coordination store

- **WHEN** a host registers a shared coordination store before or after composing its role
- **THEN** the replay state is held in that shared coordination store, and a replay requested in one
  host is seen as
  active by every host sharing it, exactly as before this requirement existed

#### Scenario: A host falls back to in-process coordination

- **WHEN** a host holds the replay state in process
- **THEN** it records a warning once, at start-up, saying that replay coordination is per process
  and naming what registers the shared coordination store

## MODIFIED Requirements

### Requirement: Publication is suppressed while a replay is active

While a replay is active, the framework SHALL suppress publication of anything the replayed events
provoke, so that historical events do not re-trigger side effects.

The suppression SHALL reach every host that shares the replay coordination state. Where a host holds
that state in process, the suppression reaches that host only; a deployment of several hosts that
needs a replay to suppress publication in all of them must register a shared coordination store.

#### Scenario: A replay provokes a dispatch

- **WHEN** a replayed event causes a command or bundle to be dispatched
- **THEN** it is not published to the bus while the replay is active

#### Scenario: Several hosts share the coordination state

- **WHEN** a replay is active in one host and another host that shares the coordination state
  dispatches a command or bundle
- **THEN** the other host's publication to the bus is suppressed as well — the dispatch itself
  still completes into durable storage

#### Scenario: A host holds the coordination state in process

- **WHEN** a replay is active in a host that holds the state in process and another host dispatches
  a command or bundle
- **THEN** the other host publishes as usual — it never learned of the replay, which is what the
  start-up warning of the first host said would happen
