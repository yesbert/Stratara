## ADDED Requirements

### Requirement: An append can be made conditional on the version the caller read

A caller SHALL be able to append to a stream on the condition that the stream is still at the version
the caller read. The events SHALL be numbered from that version onward, and where another writer has
moved the stream past it by the time of the save, the save SHALL fail with the same concurrency conflict
a version collision raises — identifying the stream and the aggregate type, clearing the staged batch,
and recording nothing of it. A version above the stream's current head SHALL be refused when the append
is made, because accepting it would leave a gap in the stream's versions.

A host SHALL be able to opt in to the same condition for every append that follows a read: where the
option is on and the caller rebuilt a stream's aggregate in the same unit of work, an append to that
stream SHALL be conditional on the version the rebuild saw. A rebuild bounded to an earlier version SHALL
NOT set the condition, and a rebuild of a stream that does not exist SHALL set the condition that it
still does not exist. A save, successful or not, SHALL clear the remembered versions, so a caller that
runs again starts from what it reads anew. The option SHALL be off by default; without it an append SHALL
keep numbering after whatever the stream holds when the append is made.

A conditional append is what closes the window between a decision and its commit: without it a fact
decided on a stream's earlier state is recorded after a fact that contradicts it — for example after the
stream's end — and nothing fails.

#### Scenario: The stream moved between the read and the save

- **WHEN** a caller reads a stream at version N, another writer appends version N+1, and the caller then
  appends conditionally on N and saves
- **THEN** the save fails with a concurrency conflict naming the stream and the aggregate type, and none
  of the caller's events is recorded — verified on the PostgreSQL and the SQLite store

#### Scenario: The stream did not move

- **WHEN** a caller appends conditionally on the version the stream is still at, and saves
- **THEN** the events are recorded from the next version onward, as an unconditional append would record
  them

#### Scenario: The caller names a version the stream has not reached

- **WHEN** a caller appends conditionally on a version above the stream's current head
- **THEN** the append is refused with a message naming the stream, the expected version and the head, and
  nothing is staged

#### Scenario: The host opts in and a handler reads before it writes

- **WHEN** the option is on, a handler rebuilds an aggregate at version N, another writer appends N+1,
  and the handler appends without naming a version and saves
- **THEN** the save fails with a concurrency conflict, and none of the handler's events is recorded

#### Scenario: The host opts in and a handler reads a stream that does not exist

- **WHEN** the option is on, a handler finds no stream, another writer creates it, and the handler then
  appends its first event and saves
- **THEN** the save fails with a concurrency conflict rather than continuing the other writer's stream

#### Scenario: The host opts in and a handler reads a past version

- **WHEN** the option is on and a handler rebuilds an aggregate only up to an earlier version, and then
  appends
- **THEN** the append is numbered after the stream's head, as without the option

#### Scenario: The host does not opt in

- **WHEN** the option is off, a handler rebuilds an aggregate at version N, another writer appends N+1,
  and the handler appends and saves
- **THEN** the handler's events are recorded after N+1, as before

#### Scenario: A conflict reaches a handler delivered by the bus

- **WHEN** a conditional append's save fails in a handler that the bus delivered
- **THEN** the message is redelivered under the conflict bound, and the handler runs again in a new unit
  of work that reads the stream's current state — verified on RabbitMQ
