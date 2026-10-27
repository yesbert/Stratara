## MODIFIED Requirements

### Requirement: Chaining happens behind the write, not during it

Events SHALL be chained by a background process after they are committed, and only once they are
settled — an event more recent than a short commit-settling interval SHALL be left for the next
pass.

Chaining during the write would put a serialising dependency on the global sequence into the write
path; waiting for settlement is what stops a concurrently committing transaction from being chained
out of order.

A pass SHALL find the events still waiting to be chained, and the last chained event it continues
from, without reading the events chained before it, so that a pass costs what is waiting and not
what the store has accumulated.

#### Scenario: An event has just been committed

- **WHEN** an event was committed within the settling interval
- **THEN** it is not yet chained, and is picked up by a later pass

#### Scenario: Many events are waiting

- **WHEN** more unchained events exist than one batch covers
- **THEN** they are chained in batches until none remain, each batch continuing from the previous
  entry's hash

#### Scenario: A pass runs on a large chained store

- **WHEN** a pass runs on a store whose events are chained but for a few, whatever key the
  consumer's write context gives the event stream
- **THEN** the pass reads the waiting events and their predecessor by index, without a scan of the
  whole event table — verified on PostgreSQL 17 by the plan of each read

### Requirement: The chain head is anchored periodically

The framework SHALL record an anchor capturing the chain's head — its hash and its sequence number —
once the sequence has advanced by a fixed number of events since the last anchor. Anchors SHALL be
unique per partition and sequence.

An anchor is what turns "the chain is internally consistent" into "the chain was in this state at
this point", which is what a full re-chain cannot forge once the anchor is out of reach.

Finding the last anchor SHALL NOT read every anchor recorded before it.

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

#### Scenario: Many anchors have been recorded

- **WHEN** the store holds tens of thousands of anchors and a pass asks for the last one
- **THEN** it is found by an index over the anchors' sequence number, without sorting every anchor —
  verified on PostgreSQL 17 by the plan of the read
