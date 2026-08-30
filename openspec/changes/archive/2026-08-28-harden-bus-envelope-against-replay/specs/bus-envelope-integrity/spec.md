## MODIFIED Requirements

### Requirement: A message's identity claims are what get signed

A signature SHALL cover the message's canonical form: for a command, its type name together with its
serialized session context; for an event bundle, its serialized session context.

The session context is what a receiver acts on — the tenant, the actor, the correlation — so that is
what has to be unforgeable.

The canonical form SHALL additionally cover the message's payload, so that a signature valid for one
message cannot be presented with a different payload. Covering it as a digest keeps the signature
independent of payload size.

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

## REMOVED Requirements

### Requirement: Misconfiguration is warned about at start-up

Replaced by the requirement below rather than edited in place. Its scenario for the off-mode warning
was scoped to a host *named* production; the replacement scopes it to a host that is not in
development, which is a different condition rather than a reworded one. Restating keeps the change
honest about that.

## ADDED Requirements

### Requirement: Misconfiguration outside development is warned about at start-up

The framework SHALL warn at start-up when integrity is off outside development, and when a mode is
enabled without a signer registered. Each warning SHALL name what the exposure is and how to fix it.

Both states are silent at run time by design, so start-up is the only place they can be surfaced.

The warning SHALL be governed by whether the host is in development, not by whether it is named
production: a host named for a region or an abbreviation is a production host that a
production-only check does not recognise, and it is exactly the host whose messages travel a real
broker.

#### Scenario: Integrity is off outside development

- **WHEN** a host that is not in development starts with the mode off
- **THEN** a warning states that outbound messages carry no signature, that consumers do not verify,
  and that anyone with publish rights can forge the tenant and actor

#### Scenario: Integrity is off in development

- **WHEN** a development host starts with the mode off
- **THEN** no warning is emitted — that is the configuration development is expected to run

#### Scenario: A mode is enabled with no signer

- **WHEN** a host starts with a mode enabled and no signer registered
- **THEN** a warning states that verification silently reports skipped for every message, and that
  the signer must be registered on publisher and consumer hosts alike with a shared key

#### Scenario: The host is correctly configured

- **WHEN** a host starts with a mode enabled and a signer registered
- **THEN** no warning is emitted
