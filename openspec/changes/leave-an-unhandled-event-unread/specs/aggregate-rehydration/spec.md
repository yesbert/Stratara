## MODIFIED Requirements

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
