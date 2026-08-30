# tamper-evident-streams Specification

## Purpose
Make an alteration of a recorded event detectable after the fact — including by someone who can
write to the database directly — by binding every entry to the one before it and periodically
pinning the chain's head.

## Requirements

### Requirement: Every recorded event is chained to its predecessor

Each recorded event SHALL carry a hash of its own content together with the hash of the preceding
event, so that the sequence forms a chain in which altering any entry invalidates every entry after
it.

#### Scenario: Events are chained

- **WHEN** events have been recorded and chaining has run
- **THEN** each carries its own hash and the hash of the entry before it in global sequence

#### Scenario: The chain starts

- **WHEN** the first event in the store is chained
- **THEN** it is chained to a fixed genesis value rather than to nothing, so the chain has a
  defined beginning

### Requirement: The hash binds position and identity, not only payload

An entry's hash SHALL be computed over its predecessor's hash, its global sequence number, its
version within its stream, its timestamp, its event type name and its payload.

Hashing the payload alone would leave an entry's position forgeable: entries could be reordered, or
one substituted for another with the same content elsewhere in the store.

#### Scenario: A payload is altered

- **WHEN** a recorded payload is modified in the database
- **THEN** recomputing that entry's hash yields a different value, and every subsequent entry's hash
  no longer matches

#### Scenario: An entry is moved or removed

- **WHEN** an entry is deleted from the middle of the sequence, or its position changed
- **THEN** the chain breaks at that point, because position is part of what was hashed

### Requirement: Chaining happens behind the write, not during it

Events SHALL be chained by a background process after they are committed, and only once they are
settled — an event more recent than a short commit-settling interval SHALL be left for the next
pass.

Chaining during the write would put a serialising dependency on the global sequence into the write
path; waiting for settlement is what stops a concurrently committing transaction from being chained
out of order.

#### Scenario: An event has just been committed

- **WHEN** an event was committed within the settling interval
- **THEN** it is not yet chained, and is picked up by a later pass

#### Scenario: Many events are waiting

- **WHEN** more unchained events exist than one batch covers
- **THEN** they are chained in batches until none remain, each batch continuing from the previous
  entry's hash

### Requirement: The chaining process survives its own failures

The background process SHALL continue running after a failure, recording it, rather than stopping.
An unchained event SHALL remain unchained rather than being skipped, so a later pass picks it up.

#### Scenario: A pass fails

- **WHEN** a chaining pass fails
- **THEN** the failure is recorded and the process continues with the next pass

#### Scenario: The host is shutting down

- **WHEN** the host is stopping
- **THEN** the process stops and records that it stopped

### Requirement: The chain head is anchored periodically

The framework SHALL record an anchor capturing the chain's head — its hash and its sequence number —
once the sequence has advanced by a fixed number of events since the last anchor. Anchors SHALL be
unique per partition and sequence.

An anchor is what turns "the chain is internally consistent" into "the chain was in this state at
this point", which is what a full re-chain cannot forge once the anchor is out of reach.

#### Scenario: The sequence advances past the anchor interval

- **WHEN** the chain head's sequence number exceeds the last anchor's by at least the anchor
  interval
- **THEN** an anchor is recorded carrying that head's hash, sequence number, partition and tenant

#### Scenario: The sequence has not advanced far enough

- **WHEN** the gap is smaller than the anchor interval
- **THEN** no anchor is recorded

#### Scenario: Nothing has been chained yet

- **WHEN** no chained event exists
- **THEN** no anchor is recorded

### Requirement: The framework preserves the evidence but does not check it

The framework SHALL NOT verify the chain on its own. Verification — recomputing the chain and
comparing — is a deliberate pass a consumer runs on a schedule it chooses.

This is stated as a requirement because the opposite is the natural assumption. A consumer that
believes the framework checks continuously has an unmonitored control, and the whole value of the
mechanism is the verification that nobody ran.

#### Scenario: An entry is tampered with and nothing verifies

- **WHEN** a recorded entry is altered and no verification pass is run
- **THEN** the framework does not detect it, raise anything, or fail any operation — the evidence is
  preserved and unexamined

#### Scenario: A consumer runs a verification pass

- **WHEN** a consumer recomputes the chain and compares it against what is recorded
- **THEN** a divergence identifies the sequence number at which the chain first breaks

### Requirement: External anchoring is a seam, not a shipped integration

An anchor SHALL carry a field for an external commitment reference, and the framework SHALL NOT
submit anchors anywhere or verify against an external source. Choosing the external source of truth,
submitting to it and re-verifying against it are the consumer's.

Without an externally committed anchor, an attacker who controls the database can rewrite every
entry's hash and produce a stream that verifies perfectly. The chain raises the cost of tampering;
only the external anchor makes it detectable.

#### Scenario: No external anchoring is wired

- **WHEN** an attacker with write access rewrites an entry and re-chains every entry after it
- **THEN** local verification passes, and the tampering is not detectable from within the system

#### Scenario: Anchors have been committed externally

- **WHEN** anchors have been committed to a source the attacker does not control, and a re-chained
  stream is verified against them
- **THEN** the re-chained stream's anchor hashes do not match the committed ones

#### Scenario: The attacker controls the external pipeline too

- **WHEN** the attacker can forge the external commitment as well
- **THEN** the mechanism does not help — the guarantee depends entirely on the anchor target being
  outside the attacker's reach
