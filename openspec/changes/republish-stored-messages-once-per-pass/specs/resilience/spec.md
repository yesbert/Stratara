## MODIFIED Requirements

### Requirement: Dispatch policies retry a bounded number of times

The command-dispatch and event-bundle-dispatch policies SHALL retry a small, bounded number of
times with exponential, jittered backoff, and then surface the failure.

Unlike bus traffic, a dispatch failure has a fallback — the outbox — so retrying forever would keep
work in memory that belongs in durable storage.

The policies SHALL govern a first dispatch only. Republishing a message from durable storage SHALL make
one attempt and SHALL NOT run under them.

#### Scenario: Dispatch keeps failing

- **WHEN** a dispatch fails on every attempt
- **THEN** the policy stops after its bounded attempts and the failure reaches the caller, which can
  fall back to durable storage

#### Scenario: A stored message is republished

- **WHEN** a message is republished from durable storage and the bus refuses it
- **THEN** the dispatch policy does not retry it, and the message stays stored for the next pass
