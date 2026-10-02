## ADDED Requirements

### Requirement: Whether a replay is active is answered without waiting on the coordination store

Every path that asks whether a replay is active — a command's dispatch, a bundle's publication, a
store reader's catch-up, the outbox drain's pass, a rebuild's refusal — SHALL be answered from what the
host already holds in memory and SHALL NOT wait on the shared coordination store for the answer, so that
a host with few cores does not spend its thread pool on a question whose answer is "no" almost always.

A host that shares the coordination state SHALL learn of a change of the marking — a replay that began,
one that ended, one whose marking lapsed because its host stopped renewing it — within a bounded period.
The period SHALL be configurable in the same section as the lease, SHALL default to a value well below
the lease, and SHALL be refused when the host starts at zero or below and at or above the lease. A change
the host made itself SHALL be seen in that host at once. Before a host has first learned the marking it
SHALL answer that no replay is active. A host that cannot reach the coordination store SHALL keep the
last answer it had rather than fail the path that asked, SHALL record once that it did, and SHALL record
once that it learned the marking again.

#### Scenario: A replay begins in another host

- **WHEN** a replay is marked active in one host, and another host shares the coordination store
- **THEN** the other host answers that a replay is active within the bounded period — verified on the
  Redis-backed coordination store

#### Scenario: A replay ends in another host

- **WHEN** a replay is marked inactive, or failed, in one host, and another host shares the
  coordination store
- **THEN** the other host answers that no replay is active within the bounded period

#### Scenario: The marking lapses

- **WHEN** the marking lapses because the replaying host stopped renewing it
- **THEN** every host sharing the coordination store answers that no replay is active within the
  bounded period of the lapse

#### Scenario: The host asks on its hot paths

- **WHEN** a host asks whether a replay is active, however often
- **THEN** no request reaches the coordination store for the answer

#### Scenario: The coordination store cannot be reached

- **WHEN** the coordination store cannot be reached while a host asks whether a replay is active
- **THEN** the host answers what it last learned, the path that asked proceeds, and the host records
  once that it cannot refresh its answer and once when it can again

## MODIFIED Requirements

### Requirement: Publication is suppressed while a replay is active

While a replay is active, the framework SHALL suppress publication of anything the replayed events
provoke, so that historical events do not re-trigger side effects.

The suppression SHALL reach every host that shares the replay coordination state, within the bounded
period in which a host learns of a change of the marking (*Whether a replay is active is answered without
waiting on the coordination store*). Where a host holds that state in process, the suppression reaches
that host only; a deployment of several hosts that needs a replay to suppress publication in all of them
must register a shared coordination store.

#### Scenario: A replay provokes a dispatch

- **WHEN** a replayed event causes a command or bundle to be dispatched
- **THEN** it is not published to the bus while the replay is active

#### Scenario: Several hosts share the coordination state

- **WHEN** a replay is active in one host and another host that shares the coordination state
  dispatches a command or bundle once it has learned of the replay
- **THEN** the other host's publication to the bus is suppressed as well — the dispatch itself
  still completes into durable storage

#### Scenario: A host holds the coordination state in process

- **WHEN** a replay is active in a host that holds the state in process and another host dispatches
  a command or bundle
- **THEN** the other host publishes as usual — it never learned of the replay, which is what the
  start-up warning of the first host said would happen
