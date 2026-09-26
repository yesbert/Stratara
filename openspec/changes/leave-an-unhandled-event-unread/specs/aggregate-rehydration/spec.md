## MODIFIED Requirements

### Requirement: An unhandled event is skipped rather than rejected

Where an aggregate declares no handler for an event in its stream, rebuilding SHALL skip that event
and continue.

Skipping SHALL NOT read the event: whether the rest of the stream can be applied SHALL depend neither
on the skipped event's type being registered in the host nor on its payload being decryptable.
Whether the aggregate handles an event SHALL be decided on the event's type as it stands after
upcasting, so an event upcast into a type the aggregate handles is applied.

An event the aggregate might apply SHALL NOT be skipped for being unreadable; it SHALL fail as an
unregistered type fails. An event is one the aggregate might apply when its recorded type has the
name of a type the aggregate handles, whatever assembly the record names. Rebuilding SHALL also read
every event in two further cases:

- the aggregate declares a handler for a type other types can derive from;
- a type the aggregate handles is not itself registered.

In both, an unreadable event fails the rebuild as it would without this rule.

An aggregate must be able to ignore facts it does not care about, and a stream must remain
replayable after an event type is retired from an aggregate's interest. Neither holds if the event is
read before it is skipped. Registering an aggregate trusts the types its handlers take, so the event
an aggregate ignores is the one most likely to be unregistered. The boundary is drawn where it is
because skipping an event the aggregate would have applied corrupts its state without a trace. A
failure is loud and can be repaired.

#### Scenario: The stream contains an event the aggregate does not handle

- **WHEN** an aggregate is rebuilt from a stream containing an event type it declares no handler for
- **THEN** that event is skipped and the remaining events are applied

#### Scenario: The unhandled event's type was never registered in the host

- **WHEN** an aggregate is rebuilt from a stream containing an event it declares no handler for, and
  nothing in the host registers that event's type
- **THEN** the rebuild succeeds, that event is skipped and the remaining events are applied

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

- **WHEN** a recorded event's type name matches a type the aggregate handles, but it names a different
  assembly and does not resolve
- **THEN** the rebuild fails, naming the type, as it does for any unregistered type

#### Scenario: The aggregate handles a type other types can derive from

- **WHEN** an aggregate declares a handler for a type that is not sealed, and its stream holds an event
  whose type is not registered
- **THEN** the rebuild fails, naming the type, as it does without this rule

#### Scenario: A handled type is not registered

- **WHEN** an aggregate was registered by a route that did not register the types its handlers take,
  and its stream holds an event whose type is not registered
- **THEN** the rebuild fails, naming the type, as it does without this rule
