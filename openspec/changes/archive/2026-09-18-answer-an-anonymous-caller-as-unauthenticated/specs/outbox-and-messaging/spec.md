# outbox-and-messaging

## MODIFIED Requirements

### Requirement: A message carries its originating session and may be signed

Every dispatched message SHALL carry the session context under which it was created. Where a signer
is configured, it SHALL additionally carry a signature over the message's canonical form.

#### Scenario: A command is dispatched

- **WHEN** a command is dispatched
- **THEN** the message carries the correlation, causation, actor and data-owner identities of the
  session that produced it

#### Scenario: No session is set

- **WHEN** a command is dispatched with no session context
- **THEN** dispatch fails rather than producing an unattributable message, with a failure that
  identifies itself as a missing identity — on the bus path and on the execution model's path alike
