## MODIFIED Requirements

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
