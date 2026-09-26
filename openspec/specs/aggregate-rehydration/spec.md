# aggregate-rehydration Specification

## Purpose
Turn a stream of recorded facts back into the object a handler reasons about, at any point in its
history, fast enough that a long-lived aggregate does not get slower forever.

## Requirements

### Requirement: An aggregate is rebuilt by replaying its stream

Rebuilding SHALL construct a fresh aggregate and apply the stream's events to it in version order.
The result SHALL reflect every event applied.

#### Scenario: A stream with events is rebuilt

- **WHEN** an aggregate is rebuilt from a stream containing events
- **THEN** every event is applied in version order and the resulting state reflects all of them

#### Scenario: A stream that does not exist is rebuilt

- **WHEN** rebuilding is requested for a stream that does not exist
- **THEN** no aggregate is returned, rather than an empty one — "never existed" and "exists and is
  empty" are different answers

#### Scenario: A stream exists but yields no events in range

- **WHEN** a stream exists but the requested version range contains no events
- **THEN** a default-constructed aggregate is returned

### Requirement: History can be replayed to a point in the past

Rebuilding SHALL accept an upper version bound, and SHALL return the aggregate as it stood at that
version.

#### Scenario: An aggregate is rebuilt as of an earlier version

- **WHEN** rebuilding is bounded to a version earlier than the stream's current one
- **THEN** the result reflects only events up to and including that version

### Requirement: Events are dispatched to the aggregate by their own type

An event SHALL be applied by invoking the aggregate's handler for that event's type. The framework
SHALL accept a handler taking the event payload directly, and a handler taking the event with its
envelope, preferring the former.

Registering an aggregate through discovery SHALL trust the payload type of every handler, including
a handler that takes the enveloped event, so that a stream holding that payload can be read.

#### Scenario: The aggregate handles the payload

- **WHEN** the aggregate declares a handler taking the event payload
- **THEN** that handler is invoked with the payload

#### Scenario: The aggregate handles the enveloped event

- **WHEN** the aggregate declares no payload handler but declares one taking the enveloped event
- **THEN** that handler is invoked with the envelope, so the aggregate can read the event's
  metadata as well as its payload

#### Scenario: An aggregate with an enveloped handler is registered by discovery

- **WHEN** an aggregate whose only handler for an event takes the enveloped event is registered
  through discovery
- **THEN** the event's payload type is trusted, and a stream holding it is rebuilt without any further
  registration

### Requirement: An unhandled event is skipped rather than rejected

Where an aggregate declares no handler for an event in its stream, rebuilding SHALL skip that event
and continue.

Skipping SHALL NOT resolve the event's type or decrypt its payload. Whether the rest of the stream can
be applied SHALL therefore depend neither on the skipped event's type being registered in the host nor
on its payload being decryptable. Whether the aggregate handles an event SHALL be decided on the
event's type as it stands after upcasting, and the way the aggregate's handlers are bound when events
are applied. An event upcast into a type a handler takes is applied. So is an event of a registered
type that a handler accepts through a base type or an interface.

An event whose type does not resolve in the host cannot be checked against the handlers. Such an event
SHALL be read, and SHALL fail as an unregistered type fails, where it could still be one the aggregate
applies:

- where its type's name, without namespace or assembly, is the name of a type one of the aggregate's
  handlers takes;
- where one of the aggregate's handlers takes an interface, an abstract class, any object or a generic
  type, or is itself generic.

Every other event whose type does not resolve SHALL be skipped. The skip SHALL be logged as a warning
naming the aggregate type and the recorded type, once per host for each pair. Where the host replaces
how recorded events are mapped, rebuilding SHALL read every event as it would without this rule.

An aggregate must be able to ignore facts it does not care about, and a stream must remain
replayable after an event type is retired from an aggregate's interest. Neither holds if the event is
read before it is skipped. Registering an aggregate trusts the types its handlers take, so the event
an aggregate ignores is the one most likely to be unregistered. The boundary is drawn where it is
because skipping an event the aggregate would have applied corrupts its state. A handled type moved
to another namespace keeps its name and fails loudly. A renamed one leaves a warning, not silence.

#### Scenario: The stream contains an event the aggregate does not handle

- **WHEN** an aggregate is rebuilt from a stream containing an event type it declares no handler for
- **THEN** that event is skipped and the remaining events are applied

#### Scenario: The unhandled event's type was never registered in the host

- **WHEN** an aggregate is rebuilt from a stream containing an event it declares no handler for, and
  nothing in the host registers that event's type
- **THEN** the rebuild succeeds, that event is skipped, the remaining events are applied, and a
  warning names the aggregate type and the event's type

#### Scenario: The warning is given once

- **WHEN** the same host rebuilds aggregates of one type many times over events of one unresolvable,
  unhandled type
- **THEN** the warning is logged once for that pair

#### Scenario: An event type is retired from the aggregate

- **WHEN** an aggregate no longer declares a handler for an event type recorded in its stream, and the
  type is no longer registered, or no longer exists
- **THEN** the stream is still rebuilt, and the retired events are skipped

#### Scenario: The unhandled event can no longer be decrypted

- **WHEN** an aggregate is rebuilt from a stream containing an event it declares no handler for, and
  that event's payload was encrypted under a key that has since been erased
- **THEN** the rebuild succeeds without attempting to decrypt that event

#### Scenario: An event is upcast into a type the aggregate handles

- **WHEN** a recorded event's type is not handled by the aggregate, and an upcaster turns it into a
  type the aggregate does handle
- **THEN** the upcast event is applied

#### Scenario: An event is rebuilt while a snapshot is being taken

- **WHEN** events that include one the aggregate does not handle, of a type the host never registered,
  are saved at a moment the snapshot policy takes a snapshot
- **THEN** the save succeeds, and the snapshot is taken as it would be without that event

#### Scenario: An unreadable event carries the name of a handled type

- **WHEN** a recorded event's type has the name of a type the aggregate handles, but names a different
  namespace or assembly and does not resolve
- **THEN** the rebuild fails, naming the type, as it does for any unregistered type

#### Scenario: A handled type is not registered

- **WHEN** an aggregate was registered by a route that did not register the types its handlers take,
  and its stream holds an event of such a type
- **THEN** the rebuild fails, naming the type, as it does without this rule

#### Scenario: The aggregate handles an interface

- **WHEN** an aggregate declares a handler for an interface, and its stream holds one event of a
  registered type implementing it and one event whose type does not resolve
- **THEN** the first is applied, and the rebuild fails on the second, naming its type, as it does
  without this rule

#### Scenario: The host replaces how recorded events are mapped

- **WHEN** a host registers its own mapping of recorded events
- **THEN** rebuilding reads every event, as it does without this rule

### Requirement: Snapshots shorten a replay without changing its result

Where a snapshot exists for a stream, rebuilding SHALL start from that snapshot's state and apply
only the events after it. The result SHALL be the same as replaying from the beginning.

#### Scenario: A snapshot exists

- **WHEN** an aggregate is rebuilt and a snapshot exists at some version
- **THEN** the snapshot's state is the starting point, and only events after that version are read
  and applied

#### Scenario: No snapshot exists

- **WHEN** no snapshot exists for the stream
- **THEN** rebuilding replays the whole stream

#### Scenario: Rebuilding is bounded to a version before the snapshot

- **WHEN** rebuilding is bounded to a version earlier than the latest snapshot
- **THEN** a snapshot no later than that bound is used, so the bound is honoured

### Requirement: Snapshots are looked up for the aggregate type being rebuilt

A snapshot SHALL be selected by both the stream and the aggregate type being rebuilt, so that two
different aggregate types projected from the same stream do not read each other's snapshots.

#### Scenario: Two aggregate types share a stream

- **WHEN** two different aggregate types are rebuilt from the same stream
- **THEN** each uses only snapshots written for its own type

### Requirement: An unreadable snapshot fails loudly

Where a snapshot exists but cannot be restored into the aggregate type, rebuilding SHALL fail rather
than fall back to a full replay.

A silent fallback would turn a schema mismatch into a permanent, invisible performance loss, and
would hide the case where the snapshot and the events disagree.

#### Scenario: A snapshot cannot be deserialized

- **WHEN** a snapshot exists but cannot be restored into the requested aggregate type
- **THEN** rebuilding fails, naming the aggregate type and the stream

### Requirement: When to snapshot is a configurable policy

Whether to write a snapshot after a batch SHALL be decided by a replaceable policy, given the
aggregate type, the stream's current version and the version of its last snapshot. The framework
SHALL ship a policy that snapshots once a configurable number of versions has accrued since the last
one, and a policy that never snapshots.

#### Scenario: The version gap reaches the threshold

- **WHEN** the current version exceeds the last snapshot's version by at least the configured
  threshold
- **THEN** a snapshot is written

#### Scenario: The version gap is below the threshold

- **WHEN** the gap is smaller than the threshold
- **THEN** no snapshot is written

#### Scenario: Snapshotting is disabled

- **WHEN** the never-snapshot policy is in effect
- **THEN** no snapshot is ever written, whatever the version gap

#### Scenario: A consumer supplies its own policy

- **WHEN** a consumer registers its own policy
- **THEN** that policy decides, including for gaps the shipped policy would not have snapshotted at

### Requirement: A snapshot captures state per stream and aggregate type

Snapshot evaluation SHALL treat each stream and aggregate type in a saved batch independently, and a
written snapshot SHALL record the aggregate's state at the batch's highest version for that stream,
protected under the stream's owning tenant.

#### Scenario: One save touches several streams

- **WHEN** a batch contains events for more than one stream
- **THEN** each stream is evaluated for snapshotting on its own, and a snapshot for one does not
  imply a snapshot for another

#### Scenario: A snapshot is written

- **WHEN** a snapshot is written
- **THEN** it records the aggregate as of the highest version in the batch, serialized under the
  owning tenant recorded on the stream

### Requirement: An aggregate's state must be restorable from its serialized form

An aggregate SHALL expose its state so that it can be written to a snapshot and read back — its
properties must be settable from outside the type. Where a registered aggregate declares a property
that holds state and cannot be set from outside, the host SHALL refuse to start, naming the
aggregate and the property.

Refusing to start is the point. The failure this replaces is silent: the aggregate rebuilds, the
events after the snapshot apply, and only the state the snapshot held is missing — so the damage
grows the better snapshotting works and nothing reports it.

A property that holds no state of its own — one computed from other properties — is unaffected,
because a restore that omits it loses nothing.

#### Scenario: An aggregate is restored from a snapshot

- **WHEN** an aggregate whose properties are publicly settable is restored from a snapshot
- **THEN** its state matches what was captured

#### Scenario: A property cannot be set from outside

- **WHEN** a registered aggregate declares a state-holding property that cannot be set from outside
  the type
- **THEN** the host fails to start, naming the aggregate and the property, rather than starting and
  losing that property's state on the next snapshot restore

#### Scenario: A property is computed rather than stored

- **WHEN** a registered aggregate declares a property computed from its other properties, with no
  setter
- **THEN** the host starts — the property is recomputed after a restore and nothing is lost
