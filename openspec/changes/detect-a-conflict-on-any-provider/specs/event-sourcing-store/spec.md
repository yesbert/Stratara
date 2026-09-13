## MODIFIED Requirements

### Requirement: A concurrency conflict discards the batch and is distinguishable

Where a save conflicts with a concurrent writer on any stream in the batch, the framework SHALL
signal a concurrency conflict identifying the stream and aggregate type, SHALL clear the staged
batch, and SHALL record the conflict as a measurement dimensioned by aggregate type.

A conflict SHALL be distinguishable from any other persistence failure, so that a caller can retry
the former and must not retry the latter — and it SHALL be distinguishable on every database
provider the framework ships a store registration for, not only on the one it was first built on.
A version collision that the database refuses through its uniqueness constraint is a concurrency
conflict on every provider.

#### Scenario: Two writers race on one stream

- **WHEN** a save fails because another writer has already written the versions being appended
- **THEN** the failure identifies itself as a concurrency conflict, names the stream and the
  aggregate type, and the staged batch is cleared so a retry starts from a re-read

#### Scenario: Two writers race on a provider other than PostgreSQL

- **WHEN** a save fails because of a version collision on a provider the framework ships a
  registration for
- **THEN** the failure is the same concurrency conflict a PostgreSQL store signals — verified on the
  SQLite store the test-support package registers

#### Scenario: A save fails for an unrelated reason

- **WHEN** a save fails for a reason that is not a concurrency conflict
- **THEN** the failure propagates unchanged and is not presented as a conflict

#### Scenario: An operator watches for contention

- **WHEN** conflicts occur
- **THEN** each is counted, dimensioned by the aggregate type and the partition it fell in

### Requirement: Registering the store's context makes the store usable

Registering the write store's database context through the framework's registration SHALL make the
write-side unit of work available to everything that depends on it — appending events, publishing
through the outbox, handling commands in a worker — without a further registration by the consumer.
A write-side unit of work the consumer registers itself SHALL take precedence over the framework's.

The same registration SHALL make the store recognise its provider's version collision as a
concurrency conflict. A consumer that brings a provider the framework has no registration for MAY
register its own recognition of that provider's collision, and the framework SHALL consult it
alongside its own.

A store whose context is registered but whose unit of work is not is a store that fails at the first
command with an error naming a type no guide mentions. The registration that declares the context
is the one place that knows which context the unit of work should be built over — and which
provider's error means "someone else wrote first".

#### Scenario: A consumer registers only the write context

- **WHEN** a consumer registers its write-store context through the framework's registration and
  nothing else
- **THEN** the write-side unit of work resolves and is bound to that context, and a command that
  appends an event can be handled

#### Scenario: A consumer supplies its own write-side unit of work

- **WHEN** a consumer registers its own write-side unit of work, before or after registering the
  context
- **THEN** the consumer's unit of work is the one resolved

#### Scenario: The registration is applied more than once

- **WHEN** the write-store context is registered more than once for the same context type
- **THEN** one unit of work is resolved, bound to that context

#### Scenario: A consumer brings a provider the framework does not ship

- **WHEN** a consumer registers its own recognition of a provider's version collision
- **THEN** a collision that recognition identifies is signalled as a concurrency conflict, and the
  framework's own recognitions keep working beside it
