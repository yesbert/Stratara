# Ship the Orleans execution model

> **Status:** approved (owner, 2026-09-14)

## Why

Two archived changes answered the question the proof of concept was built for.
`prove-an-orleans-execution-model` (2026-09-13) showed that a virtual-actor runtime closes, by
construction, the gaps the bus workers leave open — a committed fact that never reaches a
projection, a command that is lost between acceptance and execution, a rebuild that empties every
read model — and `optimise-the-orleans-execution-model` (2026-09-14, #77) removed the costs that had
argued against making it the default: a rebuild of one projection at 17.6 % of a full replay,
processor time per command at +12 % of the bus host, one aggregate under load at parity. On that
evidence the owner decided on 2026-09-14 that **the Orleans execution model becomes the
recommended one** (recorded in the archived `evidence/results.md`). Today it is a non-packable
proof of concept that no consumer can install. This change ships it.

## What Changes

- **Two new packages in the lockstep family**, Tier-C, packable and in the publish filter: the
  execution model — aggregate grain, durable-intent dispatcher, projection and saga grains,
  singleton work, durable timers, bounded heavy work — and its Entity Framework Core persistence —
  the commit-order readers and the checkpoint store; the store-schema additions are declared by the
  shipped store package. Working names
  `Stratara.Orleans` and `Stratara.Orleans.EntityFrameworkCore`. Orleans is pinned to the range
  `[10.3.1, 11.0.0)`; an Orleans minor upgrade is a Stratara patch unless the wire format changes.
- **A new capability, `orleans-execution`**, stating what a consumer of those packages observes:
  one activation per aggregate cluster-wide with the store's version constraint as the backstop;
  an accepted command recorded before the call returns, resumed after a crash, retried a bounded
  number of times and then kept; command order per aggregate within one scope; projections and
  sagas that read the store in commit order from a checkpoint and never miss a committed fact; a
  failing entry that stops its partition, is retried without advancing the checkpoint, and is
  logged and counted; one projection rebuilt alone while the others keep applying; singleton work
  once per cluster; owner-checked durable timers that survive a restart; heavy work bounded
  cluster-wide by permits that expire with their holder; one reset; and what a silo restart and a
  join after a hard death do and do not do.
- **The store schema grows** by the commit-order columns and the partition counter on the write
  side and the checkpoint table on the read side, and the outbox record gains what a bounded
  resume needs; each is a consumer migration, named in the migration note.
- **Six limitations the proof of concept recorded are closed**: no diagnostics; permits without a
  lease; an unbounded resume; simple-name identities for readers and sagas; hosted-service
  starters; cancellation that did not reach the store. And the three items #77's review named:
  batch deletion through the outbox repository port; a start-up check for the named durable grain
  directory; a batch that says whether more exists.
- **The review of the proof of concept after #77 is closed with it**: a command on this path passes
  the mediator pipeline and the enqueue-time authorizer in either registration order; a command sent
  for another aggregate runs in that aggregate's activation; a process timeout that touches its timers
  completes, survives a kill inside its step and is not delayed by clock differences; a long handler is
  not handed over again and an encrypted command keeps its order when resumed; the partition count is
  part of a checkpoint's identity; a rebuild and a full replay leave no checkpoint past what they
  emptied; settings, timer ports and singleton placement are checked at start; the reset and the
  portable reader's backfill are designed; the published surface is trimmed to what a consumer should
  use; kill tests prove what they claim; and the packages return to the analysis gate's coverage
  measure.
- **The composites split additively.** The projection and saga composites keep their names and
  what they register; each gains a sibling that registers everything but the bus-fed worker, so
  the Orleans registrations remove nothing by name.
- **The documentation follows the specs**: pages for the new capability, the migration note and
  the operations note on the site, the AI index's core facts, and an unreleased changelog entry. The
  README, the landing page, a getting-started entry point and the release notes present the execution
  model as the release's headline feature, with measured numbers behind it (owner request,
  2026-09-14).
- **Evidence**: B3 and B5 re-run once on the packaged code with diagnostics on, against the
  archived optimised numbers; the integration suite runs against the packages.

**Not in this change:** deprecating the bus workers (they stay supported in 4.x and are removed
no earlier than the next major, by their own change); a SQL Server native reader (the portable
counter serves every other provider); Orleans streams; any change to what `ISaga`, `IProjection`,
`ICommandHandler<T>`, `IAggregateScopedCommand` or `IEventSource` promise.

## Consumer-visible effect

A consumer can install the Orleans execution model from the feed and run it beside or instead of
the bus workers. Everything a consumer has today keeps working unchanged: every existing composite
registers what it registered, every existing contract promises what it promised, and a host that
does not install the new packages notices nothing except the additions to the store schema in its
next migration — the commit-order and position columns, the counter table, the outbox record's resume
bookkeeping and the checkpoint table — none of which changes behaviour. The published surface gains
two packages, one registration per role, one optional interface for stateful processes, one for
rebuildable projections, the timer port, the reset port, the command-intent port with its registration, members on
the outbox record, and one default-implemented member on the outbox repository port. This is a **minor** bump after the
merge: new packable projects.

## Capabilities

### New Capabilities

- `orleans-execution`: what a consumer of the Orleans execution model packages observes — the
  guarantees per role, the operational shape, and the answers to a hard death.

### Modified Capabilities

- `event-sourcing-store`: *The store declares its own schema* gains the commit-order columns, the
  partition counter, the checkpoint table and the resume bookkeeping on the outbox record; a new
  requirement makes the store readable in commit order without skipping a late committer.
- `outbox-and-messaging`: *Dispatch attempts the bus first and falls back to durable storage*
  gains the durable-intent shape; *A worker drains durable storage in batches under a distributed
  lock* gains the singleton-work shape that needs no lock; a new requirement lets stored messages
  be removed as a batch through the repository port.
- `projections`: *A replay truncates every read model before rebuilding* gains the per-projection
  rebuild; *Bundles about one aggregate are applied one at a time within a process* gains the
  cluster-wide shape; new requirements state the checkpoint path and what a stalled partition
  reports.
- `sagas`: *Sagas consume the event stream through their own subscription* gains the store-reading
  shape; *Bundles about one aggregate reach sagas one at a time within a process* gains the
  cluster-wide shape; a new requirement states what a stateful process is.
- `host-composition`: *Each worker role has one composite that wires it* gains the split into
  services and bus worker, and the one registration per role the Orleans model adds.
- `observability`: *Log event ids follow a partitioned schema that reserves a consumer range* gains
  the Orleans band; a new requirement lists what the execution model measures.
- `package-distribution`: *Every package ships at one lockstep version* and *Dependencies flow one
  way and never cycle* gain the two packages and the runtime dependency range.

## Impact

- **New, packable:** `src/Stratara.Orleans/` (today the proof of concept, made packable in place)
  and `src/Stratara.Orleans.EntityFrameworkCore/` (new; today's `CommitOrder/` readers, the
  partition-counter interceptor, the checkpoint store, the portable reader's backfill and the
command-intent store). Both in
  `Stratara.Publish.slnf`.
- **Modified, packable:** `src/Stratara.Abstractions/` (a default-implemented member on the outbox
  repository port, the outbox record's resume bookkeeping, and the timer, singleton-work, reader,
  checkpoint, rebuilder and command-intent ports — design D1, D4); `src/Stratara.EventSourcing.EntityFrameworkCore/` (the
  batch delete, and the schema additions declared by the shipped write and read contexts, with the
  counter and checkpoint entities); `src/Stratara.Projections/` and `src/Stratara.Sagas/` (the
  rebuildable projection and the process); `src/Stratara.EventSourcing.WorkerDefaults/` (the sibling
  composites, D11); `src/Stratara.Infrastructure/` (the enqueue-time authorizer decorates the registered
  dispatcher, D15); `src/Stratara.Diagnostics/` (the log event band and the instruments).
- **Tests:** `tests/Stratara.Orleans.Tests/`, `tests/Stratara.Orleans.IntegrationTests/` and
  `tests/Stratara.Orleans.Benchmarks/` follow the packages; the integration suite becomes the
  packages' suite; `tests/Stratara.EventSourcing.WorkerDefaults.Tests/`,
  `tests/Stratara.Infrastructure.Tests/` and `tests/Stratara.EntityFrameworkCore.Tests/` gain the cases
  the tasks name. As implemented, the scenarios, silo and store helpers and container fixtures moved into a new
  library, `tests/Stratara.Orleans.Scenarios/`, which the integration tests and the benchmark host both reference
  (task 7.1); `tests/Stratara.WriteStore.Tests/` gained the batch-removal test,
  `tests/Stratara.Documentation.Tests/` and `tools/Stratara.ReferenceCatalogue/` reference the two packages so the
  documentation checks see them, and the PostgreSQL test fixture raises its connection limit for all-class runs.
- **CI:** `.github/workflows/sonar.yml` loses the coverage exclusion for `src/Stratara.Orleans/**` and
  collects the Orleans integration suite's coverage (D26); `.github/workflows/integration.yml` loses the
  scenario-host build step once the test project builds the host (D25); `.github/workflows/ci.yml` and
  `scripts/local-gauntlet.sh` skip the scenario library in their test loops, since it is not a test project.
- **Documentation:** `docs/` gains the capability's pages, the migration note and the operations
  note, and `docs/getting-started/choose-an-execution-model.md`; `llms.txt` and `llms-full.txt` carry
  the core facts; `README.md` and the landing page present the execution model as the headline feature
  with the measured numbers; `CHANGELOG.md` gains an unreleased entry that opens the minor's release
  notes; every page that states the package count or the log-event range follows (D13).
- **Version:** `<VersionPrefix>` is not touched here; `/bump-version minor` follows the merge.
- **Superseded sources:** `openspec/changes/archive/2026-09-13-prove-an-orleans-execution-model/evidence/migration-note.md`
  and `openspec/changes/archive/2026-09-14-optimise-the-orleans-execution-model/evidence/operations-note.md`
  — carried onto the documentation site by this change; the archived copies stay as the record of
  their day. The *Known limitations of the proof-of-concept code* list in
  `openspec/changes/archive/2026-09-13-prove-an-orleans-execution-model/design.md` is closed by
  this change, item by item, in `design.md`.
