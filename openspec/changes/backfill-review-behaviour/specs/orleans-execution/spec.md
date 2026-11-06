## ADDED Requirements

### Requirement: A command's grace outlasts the collection of its completion

`OrleansDispatchOptions.IntentGrace` SHALL be longer than three times `CompletionWindow`, and a host
configured otherwise SHALL fail at start with a message naming both. A completed command's record is no
longer renewed while its completion is collected, so a longer window would let the drain hand the
command over again and run its handler a second time.

#### Scenario: The window is a third of the grace or more

- **WHEN** a host configures a `CompletionWindow` of a third of `IntentGrace` or more
- **THEN** the host fails at start with a message naming both settings

#### Scenario: The window is shorter than a third of the grace

- **WHEN** a host configures a `CompletionWindow` shorter than a third of `IntentGrace`, or leaves both
  at their defaults of 20 ms and 30 s
- **THEN** the host starts

### Requirement: An entry is retried from a fresh scope

Where an attempt to apply an entry to a store-reading projection or saga fails, its retry SHALL resolve
the projection or saga, and what it depends on, anew, so that nothing the failed attempt left in a
scoped dependency is applied a second time.

#### Scenario: A projection's attempt left state behind

- **WHEN** a projection stages a write in a scoped dependency, reports a missing preceding fact, and the
  entry is retried
- **THEN** the retry starts from a new instance of the dependency and the staged write is not applied —
  verified on the in-process execution-model host

#### Scenario: A saga's attempt left state behind

- **WHEN** a saga stages a write in a scoped dependency, reports a missing preceding fact, and the entry
  is retried
- **THEN** the retry starts from a new instance of the dependency and the staged write is not applied —
  verified on the in-process execution-model host

### Requirement: The timer start-up check builds no saga to learn its type

The check that a stateful process has what serves its timers SHALL read a saga's type from its
registration and SHALL NOT build a saga registered as a type or an instance, so that a host whose sagas
need a session to be constructed, which none is at start, starts. A saga registered through a factory
is built to learn its type, and one that cannot be built there is passed over.

#### Scenario: A saga needs a session to be constructed

- **WHEN** a host registers a saga as a type whose dependency refuses to be constructed without a session
- **THEN** the host starts

#### Scenario: A stateful process needs a session and nothing serves its timers

- **WHEN** a host registers, as a type, a stateful process that needs a session to be constructed, and
  registers nothing that serves its timers
- **THEN** the host fails at start, naming the process and the registration that serves them
