## MODIFIED Requirements

### Requirement: Heavy work is bounded across the cluster by permits that expire with their holder

Heavy commands SHALL run in a bounded worker pool per silo and under a cluster-wide bound of
permits. A permit SHALL expire when the silo holding it is no longer a member of the cluster or its
lease has lapsed, so that crashed workers do not shrink the bound. Interactive commands SHALL NOT
queue behind heavy work.

The bound SHALL hold across the loss of the silo that keeps the permits. A keeper that takes over
after such a loss SHALL admit no new unit until every unit admitted by the lost keeper has had a
lease's time to register with it again, and a running unit whose permit was lost with its keeper
SHALL count against the bound again as soon as it has registered; a heavy command dispatched in that
window SHALL wait, as it waits when the bound is full, rather than start beside the running units.
A running unit that is refused when it registers again — one whose lease had already lapsed with the
lost keeper — SHALL keep running, because a running handler is not paused, SHALL keep asking on every
renewal until it holds a permit or ends, and SHALL be logged with an event of its own, so that an
operator can see a unit running outside the bound while it does.

#### Scenario: A worker silo dies holding permits

- **WHEN** a silo holding heavy-work permits is killed
- **THEN** its permits are released once the cluster has declared it dead or the lease has lapsed,
  and the bound is whole again

#### Scenario: Interactive commands during a heavy burst

- **WHEN** interactive commands are dispatched while the heavy pool is saturated
- **THEN** their latency stays within its no-burst range — verified with a saturated heavy pool on
  the PostgreSQL store

#### Scenario: The silo keeping the permits dies while the bound is full

- **WHEN** the silo on which the permits are kept is killed while units on another silo hold every
  permit, and further heavy commands are dispatched
- **THEN** at no moment do more units than the bound run, the running units are counted again within
  a lease of the keeper's return, and the further commands start only once the grace has passed and
  a running unit has ended — verified with two silos on the PostgreSQL membership table and the Redis
  directory

#### Scenario: A running unit is refused when it registers again

- **WHEN** a running unit registers with a returned keeper after its lease had already lapsed and the
  bound is full
- **THEN** it runs to its end, an event names the unit, and it holds a permit again as soon as one is
  free
