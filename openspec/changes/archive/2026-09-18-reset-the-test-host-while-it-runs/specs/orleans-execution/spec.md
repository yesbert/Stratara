# orleans-execution

## MODIFIED Requirements

### Requirement: A host can reset what the execution model keeps outside the event stream

A host SHALL be able to clear, deterministically, everything the execution model keeps beside the
event stream for its own deployment — the reminders of its service, the membership of its cluster, the
grain directory's entries and the checkpoints of the store-reading projections and sagas the host
registers — so that the deployment can be brought back to "nothing scheduled, nothing remembered". A
reset SHALL NOT remove a reminder, membership row or checkpoint that belongs to another deployment or
to a consumer the host does not register, and SHALL report how many of each it removed. A reset offered
by a host that goes on running — the test-support host is the one the framework ships — SHALL instead
leave every registered reader where the store's head is and report how many it moved, because a reader
returned to the beginning would apply the store again into read models the reset does not empty. The
event stream SHALL NOT be touched by a reset. A host SHALL be able to name the schema its runtime tables
live in, and a reset that finds a runtime table absent SHALL fail naming it rather than report that it
removed nothing. The documentation SHALL say that a reset that fails part-way leaves what it had not
yet removed in place, and SHALL show the reset resolved in a way that works wherever scope validation
is on.

#### Scenario: A reset is run

- **WHEN** a host runs the reset
- **THEN** no reminder of its service, membership row of its cluster, directory entry or checkpoint of
  a projection or saga it registers remains, the event stream is intact, and a restart fires nothing
  and rebuilds those checkpoints from the stream

#### Scenario: The read store holds another consumer's checkpoints

- **WHEN** a host runs the reset against a read store that also holds the checkpoints of a projection
  the host does not register
- **THEN** those checkpoints remain with the positions they had, and the report does not count them

#### Scenario: The runtime tables live in a schema

- **WHEN** a host whose reminder and membership tables live in a schema other than the connection's
  default names that schema and runs the reset
- **THEN** its reminders and membership rows are removed and counted

#### Scenario: A runtime table is absent

- **WHEN** a host runs the reset against a database in which a reminder or membership table does not
  exist under the named schema
- **THEN** the reset fails with a message naming the table, and reports no count
