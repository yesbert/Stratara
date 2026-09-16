## MODIFIED Requirements

### Requirement: A host can reset what the execution model keeps outside the event stream

A host SHALL be able to clear, deterministically, everything the execution model keeps beside the
event stream for its own deployment — the reminders of its service, the membership of its cluster, the
grain directory's entries and the checkpoints of the store-reading projections and sagas the host
registers — so that the deployment can be brought back to "nothing scheduled, nothing remembered". A
reset SHALL NOT remove a reminder, membership row or checkpoint that belongs to another deployment or
to a consumer the host does not register, and SHALL report how many of each it removed. The event
stream SHALL NOT be touched by a reset.

#### Scenario: A reset is run

- **WHEN** a host runs the reset
- **THEN** no reminder of its service, membership row of its cluster, directory entry or checkpoint of
  a projection or saga it registers remains, the event stream is intact, and a restart fires nothing
  and rebuilds those checkpoints from the stream

#### Scenario: The read store holds another consumer's checkpoints

- **WHEN** a host runs the reset against a read store that also holds the checkpoints of a projection
  the host does not register
- **THEN** those checkpoints remain with the positions they had, and the report does not count them

## ADDED Requirements

### Requirement: A read store's checkpoints belong to the consumers that read into it

A checkpoint SHALL be identified by the consumer that reads the store and the partition it reads, not
by the deployment that runs the consumer. Two deployments SHALL be able to keep their checkpoints in
one read store only when no consumer name is registered by both; store-reading sagas count as one
consumer across all deployments, so at most one deployment sharing a read store SHALL run them. The
documentation SHALL state this where a read store is configured for the execution model.

#### Scenario: Two deployments share a read store with distinct projections

- **WHEN** two deployments register store-reading projections with different names against one read
  store and both run
- **THEN** each deployment's projections advance their own checkpoints, and neither deployment's
  reading or reset changes the other's

#### Scenario: Two deployments share a read store and both run store-reading sagas

- **WHEN** two deployments that share a read store both run store-reading sagas
- **THEN** the documentation names this as unsupported, because both write the same checkpoints
