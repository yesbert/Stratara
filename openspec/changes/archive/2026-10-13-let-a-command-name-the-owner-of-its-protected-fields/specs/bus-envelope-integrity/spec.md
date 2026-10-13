## MODIFIED Requirements

### Requirement: A message's identity claims are what get signed

A signature SHALL cover the message's canonical form: for a command, its type name together with its
serialized session context; for an event bundle, its serialized session context.

The session context is what a receiver acts on — the tenant, the actor, the correlation — so that is
what has to be unforgeable.

The canonical form SHALL additionally cover the message's payload, so that a signature valid for one
message cannot be presented with a different payload. Covering it as a digest keeps the signature
independent of payload size.

Where a command names the owner of its protected fields, the canonical form SHALL also cover that
owner — its tenant and its user — because the receiver reads the command's fields for it. A signed
command SHALL NOT verify when it is presented with a different owner, or with none.

The canonical form of a command that names no owner SHALL be the form it had before a command could
name one, so that hosts on either side of that release verify each other's signatures for such
commands, and a fleet in strict mode takes the release without a pass through permissive mode.

#### Scenario: A session context is altered in transit

- **WHEN** the serialized session context of a signed message is modified
- **THEN** the canonical form differs and the signature no longer verifies

#### Scenario: A command's type is altered

- **WHEN** a signed command's declared type name is modified
- **THEN** the signature no longer verifies

#### Scenario: A signature is presented with a different payload

- **WHEN** a signature captured from one signed message is presented with a message carrying the
  same session context but different events or a different command body
- **THEN** it does not verify

#### Scenario: The payload's serialization changes between releases

- **WHEN** a later release serializes a payload differently
- **THEN** signatures on messages produced before the change still verify, because each message is
  verified against the bytes it was published with — the signature is not tied to a schema

#### Scenario: A stored event is upcast on the way in

- **WHEN** a received message's events are transformed by an upcaster
- **THEN** verification is unaffected, because it runs on the received message before any
  transformation

#### Scenario: Content is moved from one signed field into the next

- **WHEN** a message is presented whose fields carry the same characters in the same order but
  divided between the fields differently — for example a type name extended by the first characters
  of the session context
- **THEN** its canonical form differs and the signature does not verify, so the boundary between two
  signed fields cannot be moved

#### Scenario: A command's named owner is altered

- **WHEN** the owner a signed command names — its tenant or its user — is modified in transit
- **THEN** the canonical form differs and the signature no longer verifies

#### Scenario: A command's named owner is removed

- **WHEN** a signed command that names an owner is presented without one
- **THEN** the signature no longer verifies, so a receiver cannot be made to read the command for its
  session instead

#### Scenario: A command naming no owner crosses the release

- **WHEN** a command that names no owner is signed by a host on the release before a command could
  name one and verified by a host on the release after, or the other way round
- **THEN** the signature verifies
