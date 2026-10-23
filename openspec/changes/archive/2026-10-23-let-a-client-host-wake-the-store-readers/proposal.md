# Let a client host wake the store readers

> **Status:** approved

## Why

On the Orleans execution model a commit wakes the store-reading grains only on a host that runs a
store-reading role itself. That host's projection or saga registration replaces the bus bundle dispatcher
with the wake-up. Every other host keeps the bus dispatcher it was composed with.

The migration guide assumes those other hosts commit nothing. Its broker table lists the API host as
"not needed — it records commands and commits nothing itself". A consumer's API hosts and a worker do
commit: they run some command handlers in process through the mediator, and on an Orleans client the
mediator does not forward to a grain. Once the consumer took the broker away, each such commit logged
*Error publishing event bundle … Will store in outbox*, and the bundle stayed in the outbox table, where the
drain retries it for ever. A consumer reported it on 2026-10-22 (consumer finding F-025).

The consumer cannot build the wake-up itself. The dispatcher that does it is internal, and the readers it
wakes are named after the projections and sagas registered on the silo, which a client host does not have.
The consumer's workaround replaces the dispatcher with one that does nothing and lets the readers find the
facts by polling, five seconds later by default.

A command silo without a store-reading role has the same gap. The spec currently answers it with "keeps
the broker until it registers a store-reading role".

## What Changes

The consumer-visible effect is that any host joined to the cluster, as a client or as a silo, can commit
facts without a broker and have the store readers woken at once, with one registration.

- **New registration `AddStrataraStoreWakeUps()`** (`Stratara.Orleans`, next to
  `AddStrataraOrleansCommandDispatcher`). It replaces the host's bundle dispatcher with one that, after a
  commit, wakes the store readers of every projection and saga role in the cluster for each partition the
  commit touched. It does this through the host's grain factory, without hosting a reader and without
  knowing which projections or sagas exist. Like the store-reading registrations it:
  - takes `hybrid: true` to keep publishing to the bus beside the wake-up during a rollout, failing at
    registration when no bus dispatcher is registered to keep;
  - is idempotent;
  - combines with `AddStrataraProjectionGrains` and `AddStrataraSagaGrains` in either order.
- **A role no silo hosts costs nothing per commit.** When the cluster hosts no projection role or no saga
  role, the wake-up for that role is set aside for a while instead of failing on every commit. A wake-up
  that cannot be sent is lost like any other, logged at debug, and the poll finds the fact.
- **On a silo with a store-reading role** the registration adds the wake-ups of the roles that silo does
  not host. A command silo with sagas wakes the projections on other silos too, instead of leaving them to
  their poll.
- **Documentation:**
  - The migration guide's broker table gains the case of a host that commits in process. Such a host, and
    a command silo without a store-reading role, needs the broker unless it registers
    `AddStrataraStoreWakeUps()`.
  - The concepts page and `llms.txt` name the registration.

Unchanged: how readers read, checkpoint, poll and apply; the wake-up of a store-reading silo for its own
roles; the bus model; the outbox drain. A host that does not call the new registration sees no change.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `orleans-execution`: *The execution model can be adopted per role beside the bus workers* changes in
  three places:
  - a host that is not a store reader can register the wake-up;
  - such a host no longer needs the broker;
  - the registration joins the list of idempotent registrations and of those that keep the bus on request.

## Impact

- `src/Stratara.Orleans/DependencyInjection/OrleansProjectionServiceCollectionExtensions.cs`: new public
  `AddStrataraStoreWakeUps`. `ReplaceBundleDispatcher` is shared with it.
- `src/Stratara.Orleans/Projections/OrleansEventBundleDispatcher.cs`: wakes a set of wake-ups, made of
  the local nudge targets of the roles the host registered and the cluster wake-ups of the roles it did not.
- `src/Stratara.Orleans/Projections/StoreWakeUpGrain.cs` (new): one grain per role and partition, placed by
  the role's placement filter, that nudges the readers its silo registered for that partition. Also the
  client-side wake-up with its back-off for an absent role.
- `src/Stratara.Orleans/Diagnostics/`: log events for a role found absent and found again. New ids in
  `src/Stratara.Diagnostics/LogEvents.cs`.
- `docs/guides/migrate-to-the-orleans-execution-model.md` (broker table and the paragraph above it),
  `docs/guides/operate-the-orleans-execution-model.md` (a short section on wake-ups and the poll),
  `docs/concepts/orleans-execution-model.md`, `llms.txt`, `CHANGELOG.md`.
- Additive: one new public method and new internal grains. No package, dependency, tier or schema change,
  and no migration. A patch release (4.4.7).
- Rolling upgrade: a silo of the previous release has no wake-up grain. A wake-up placed on such a silo fails
  and is lost, the readers poll, and nothing else changes (`design.md`, *Migration Plan*).
- Dissolved or superseded legacy sources: none.
