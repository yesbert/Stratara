## MODIFIED Requirements

### Requirement: A failing projection stops the bundle

Where a projection handler fails, the failure SHALL propagate rather than being swallowed, so the
bundle is not acknowledged and is not treated as processed. What happens to the bundle after that
is decided by the transport's failure policy, stated in `outbox-and-messaging` → *A message a
handler cannot take is retried a bounded number of times and then kept*: the bundle is delivered
again a bounded number of times and then moved to the subscription's dead-letter destination, from
which an operator returns it once the cause is fixed. The read model is repaired by that return —
a replay is the repair of last resort, not the only one. Propagating the failure guarantees that it
is recorded and that the bundle is never counted as applied.

A projection that silently skipped a failed event would leave a read model permanently missing that
event, with nothing recording which one — a corruption that only a full replay could repair and
nothing would reveal.

#### Scenario: A projection handler fails

- **WHEN** a projection handler throws while processing a bundle
- **THEN** the failure propagates out of bundle processing and is recorded
- **AND** the bundle is not treated as processed

#### Scenario: A projection handler keeps failing

- **WHEN** a projection handler throws on every delivery of a bundle
- **THEN** the bundle ends on the projection subscription's dead-letter destination, and the read
  model receives it when the operator returns it

#### Scenario: A bundle contains no events

- **WHEN** an empty bundle arrives
- **THEN** processing completes without invoking anything
