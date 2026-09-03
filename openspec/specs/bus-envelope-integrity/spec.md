# bus-envelope-integrity Specification

## Purpose
Stop a message that nobody trustworthy sent from being acted on — because the session context every
bus message carries decides which tenant is written to, and a broker's publish rights are not an
authorization decision.

## Requirements

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

### Requirement: Verification has three modes with an explicit rollout path

The framework SHALL offer three enforcement modes: off, which verifies nothing; permissive, which
verifies and records a failure but still delivers; and strict, which refuses a message that does not
verify.

Permissive exists so a fleet can be rolled out: publishers start signing while consumers still
accept unsigned messages, and only once every publisher signs does strict become safe. Going
straight to strict would drop every message in flight during the deployment.

A recorded failure SHALL state which of two things happened: the message carried no signature, or it
carried one that did not verify. The two SHALL be distinguishable by the identity of the record, not
only by its text, so that an operator can alert on one without the other. This SHALL hold on every
path that consumes a bus message, and in permissive and strict mode alike.

The two cases mean different things. An unsigned message is what a rolling deployment produces
between the first consumer that verifies and the last publisher that signs; after the roll it is the
signal that a publisher was missed. A signature that is present and does not verify means a publisher
holds a different key, or somebody is publishing messages they cannot sign. A record that does not
say which one it is describes an attack when it is describing a restart, and describes a restart when
it is describing an attack.

#### Scenario: A valid signature arrives in strict mode

- **WHEN** a correctly signed message arrives and the mode is strict
- **THEN** it is verified and dispatched

#### Scenario: A signature is missing in strict mode

- **WHEN** a message with no signature arrives and the mode is strict
- **THEN** it is refused and not dispatched, and the refusal states that the message was unsigned

#### Scenario: A signature is invalid in strict mode

- **WHEN** a message whose session context was tampered with arrives and the mode is strict
- **THEN** it is refused and not dispatched, and the refusal states that the signature did not verify

#### Scenario: A signature is missing in permissive mode

- **WHEN** an unsigned message arrives and the mode is permissive
- **THEN** it is dispatched anyway, and a record states that the message was unsigned, so an
  operator can see which publishers are not yet signing

#### Scenario: A signature is invalid in permissive mode

- **WHEN** a message whose signature does not verify arrives and the mode is permissive
- **THEN** it is dispatched anyway, and a record distinct from the unsigned one states that the
  signature did not verify

#### Scenario: An operator alerts on one case only

- **WHEN** an operator configures an alert on the record of an invalid signature
- **THEN** unsigned messages arriving during a rolling deployment do not raise it

#### Scenario: The mode is off

- **WHEN** the mode is off
- **THEN** no verification is performed, even where a signer is registered

### Requirement: Verification is skipped, not failed, when no signer is registered

Where no signer is registered, verification SHALL report that it was skipped rather than refusing
messages, in every mode.

Failing closed here would take a host down for a configuration mistake rather than an attack, and
the mistake is caught by the start-up warning instead.

#### Scenario: A mode is enabled but no signer is registered

- **WHEN** the mode is permissive or strict and no signer is registered
- **THEN** every message is delivered and verification reports itself as skipped

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

### Requirement: The signing key is validated and held privately

A signer SHALL refuse to be constructed without a key of at least the length its algorithm requires,
and SHALL hold a private copy so that a caller mutating the configured array afterwards cannot
change what is signed.

#### Scenario: No key is configured

- **WHEN** a signer is constructed with no key
- **THEN** construction fails, naming the minimum length and where to set it

#### Scenario: The key is too short

- **WHEN** the configured key is shorter than the minimum
- **THEN** construction fails

### Requirement: Signature comparison does not leak timing

Comparing a presented signature against the expected one SHALL not reveal, through timing, how much
of it matched.

#### Scenario: An attacker submits guessed signatures

- **WHEN** signatures that differ from the expected one at different positions are verified
- **THEN** the comparison takes the same time, so guessing byte by byte gains nothing

#### Scenario: No signature is presented

- **WHEN** the presented signature is absent or empty
- **THEN** verification fails without further work

### Requirement: Inbound messages are bounded before they are parsed

The framework SHALL reject an inbound message whose serialized size exceeds a configured limit, and
SHALL bound the nesting depth its parser will accept. Both limits SHALL have documented defaults and
be configurable.

Signature verification does not help here: the message must be parsed before it can be verified, so
the parser is reachable by anyone who can publish.

#### Scenario: A message exceeds the size limit

- **WHEN** an inbound message's serialized form is larger than the configured limit
- **THEN** it is rejected, and the failure names the size, the limit and where the message came from

#### Scenario: A message is exactly at the limit

- **WHEN** an inbound message is exactly the configured size
- **THEN** it is accepted — the limit is inclusive

#### Scenario: A message nests deeply

- **WHEN** an inbound message nests more deeply than the configured maximum
- **THEN** parsing refuses it rather than recursing

#### Scenario: No limits are configured

- **WHEN** a host configures neither limit
- **THEN** documented defaults apply, so a host is never unbounded by omission

### Requirement: Verification applies on every path that consumes a bus message

Every consumer of a bus message — the command worker, the projection worker and the saga worker —
SHALL apply the same verification.

Signing on publication while only one consumer verifies leaves the others open, which is precisely
the defect this mechanism was introduced to fix.

#### Scenario: An event bundle reaches a projection or saga worker

- **WHEN** an event bundle arrives at a worker that builds read models or runs process managers
- **THEN** it is verified under the configured mode before any handler sees it

#### Scenario: A command reaches the command worker

- **WHEN** a command arrives at the command worker
- **THEN** it is verified under the configured mode before it is dispatched
