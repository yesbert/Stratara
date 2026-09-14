# Design — Close the gap between commit and publish

## Context

See `proposal.md` → *Why*. The state on `main` at `1476af9`, established for finding SF-002 of
`prove-an-orleans-execution-model` and confirmed there by test T1.

**The save.** `EventSource.SaveChangesAsync`
(`src/Stratara.Infrastructure/EventSourcing/EventSource.cs:145-172`) starts a transaction on the
write unit of work, adds the entries and a snapshot if due, commits (line 155), and only then calls
`PublishEventBundleAsync` (line 170), which hands the bundle to `IEventBundleOutboxDispatcher`.

**The dispatcher.** `EventBundleOutboxDispatcher.EnqueueEventBundleAsync`
(`src/Stratara.Outbox.RabbitMQ/Outbox/EventBundleOutboxDispatcher.cs:42-54`) tries the bus unless a
replay is active, and on refusal opens *its own* transaction on the same unit of work and writes the
outbox row. `IWriteUnitOfWork.CreateOutboxRepository(ITransaction)`
(`src/Stratara.Abstractions/Abstractions/Persistence/IWriteUnitOfWork.cs:30`) already takes the
transaction the repository writes under — the save could hand it the transaction it holds.

**The drain.** `EnqueueOutboxEntriesAsync` (line 57) publishes stored bundles and deletes each on
acceptance, under the outbox worker's lock; `outbox-and-messaging` → *A worker drains durable
storage in batches under a distributed lock* governs it. Nothing there needs to know how a row got
into the table.

**What T1 measured.** 20 kills between commit and publish, 20 bundles never delivered on the bus
path; 0 lost on a path that reads the store from a checkpoint (`prove-an-orleans-execution-model`,
`evidence/results.md`, `raw/commit-publish-kill/20260913-152919/result.json`). That path is the
Orleans execution model and is not what this change is about; this change is for the bus path.

**What B1 measured.** The PostgreSQL store appends at about 2 600 appends/s spread over buckets at
8–32 writers and 1 700–1 900 on one bucket (`raw/append-throughput/20260913-152935/result.json`).
A second insert in the same transaction is the cost to estimate against.

## Goals / Non-Goals

**Goals:**

- The specification says what the bus path guarantees today, with the window named.
- A host can close the window with one option and no other change.
- On that option, a save is atomic over events and bundle, and never fails after its commit.
- The drain, the lock and the outbox table are untouched.

**Non-Goals:**

- Closing the window for commands dispatched through `ICommandOutboxDispatcher`. A command has no
  commit to be atomic with; bus-first is the right shape there and stays.
- Changing the default in 4.x. The hard constraint of the finding's change — no silent change of
  semantics, no throughput change a consumer did not ask for — rules it out; see D1.
- The Orleans execution model's checkpoint path, which closes the same gap differently and is
  offered by `prove-an-orleans-execution-model`.

## Decisions

### D1 — The evaluation: durable bundles are worth shipping, as an opt-in, default off in 4.x

The owner asked for an evaluation of writing the outbox row in the commit transaction. The result:

**What it costs.** One insert per save in the write transaction and one delete per accepted bundle
after it. The insert is a row of the size of the bundle (the events are serialised into it, as the
fallback already does), on a table with no unique constraint beyond its key, in a transaction that
already holds a row lock per appended entry. Against B1's numbers this is a second statement in a
transaction that today runs one; the PoC's portable counter, which added an *update under a lock*
per append, halved throughput on one bucket and cost 44 % spread — a contended update, not a plain
insert, and the worst case this change can be compared to. A plain insert is expected to cost well
under that; task 4.1 measures it with the PoC's append benchmark before the option is documented as
cheap. The delete is a second round trip after the bus accepted, off the caller's critical path if
done after the publish returns, on it if done before `SaveChangesAsync` returns — D3 decides.

**What it buys.** The window is closed by construction: a bundle whose events are durable is in the
outbox until the bus has accepted it. The failure T1 measured cannot happen. The
*additional observation* of SF-002 — bus refuses, then the outbox write fails, and the caller sees
an exception for facts that are stored — cannot happen either, because the row is written before
the commit and the bus is tried after it.

**Why not the default in 4.x.** Every host that saves events would see a row written and deleted per
save and a throughput change without having changed a line. That is the silent change of semantics
the finding's change forbids, and the outbox table's growth pattern changes for operators who watch
it. An opt-in with the default unchanged is a patch-level addition; flipping the default is a 5.0
decision the measurement in task 4.1 informs.

**Why not leave it at a spec statement.** A saga has no replay; a lost bundle on the saga side is a
process that never completes. A consumer who runs sagas on the bus path and reads the new spec
scenario has no remedy without this option.

*Alternative considered:* publish before the commit. Consumers would receive facts that may still
roll back — the "no fact overtakes its beginning" guarantee inverted. Rejected.

*Alternative considered:* a store-side hint (a "last unpublished sequence" per partition) that the
drain scans instead of an outbox row. That is the checkpoint reader of the Orleans path in
disguise and needs the commit-order columns; too much for the bus path to carry.

Evidence: T1 and B1 in `prove-an-orleans-execution-model/evidence/results.md`; the code paths
above.

### D2 — The option lives on the outbox options and is read by the event source through a port

`OutboxOptions` (`src/Stratara.Outbox.RabbitMQ/Outbox/OutboxOptions.cs`) is in the transport
package and `EventSource` in `Stratara.Infrastructure` must not know a transport. The event source
asks the dispatcher: `IEventBundleOutboxDispatcher` gains

```csharp
Task StoreEventBundleAsync(EventBundle eventBundle, ITransaction transaction, CancellationToken ct);
bool StoresBundlesWithCommit { get; }
```

Both default-implemented on the interface (`StoresBundlesWithCommit => false`,
`StoreEventBundleAsync` throwing `NotSupportedException`) so a consumer's own dispatcher keeps
compiling and keeps bus-first. The RabbitMQ dispatcher implements both from
`OutboxOptions.DurableBundles` (default `false`).

The save becomes: map and sign the bundle; start transaction; add entries and snapshot; if
`StoresBundlesWithCommit`, `StoreEventBundleAsync(bundle, transaction)`; commit; then
`EnqueueEventBundleAsync` with the same bundle instance. The dispatcher, when durable bundles are
on, publishes and on acceptance deletes the row it stored — it knows the row's id because it wrote
it. *Changed during apply, 2026-09-14:* the id does not travel on the bundle record. The dispatcher
is scoped, the event source is scoped, and the same bundle instance goes through both calls, so a
per-scope map from bundle instance to stored id is enough and `Stratara.Contracts` stays untouched —
no optional property on the wire-level record, no constructor change. The repository gains a
default-implemented `AddAsync(Guid id, …)` overload so the dispatcher can choose the id it will
delete by. Mapping the bundle before the transaction opens also means a save with no session fails
before anything is committed, where it used to fail after the commit.

*Alternative considered:* a separate `IEventBundleWriteAhead` port. One more registration for the
same component; the dispatcher is the thing that knows whether the bus was tried and whether it
accepted, so the delete belongs there.

### D3 — The delete happens after acceptance, inside the save's call, and its failure is not the save's failure

`EnqueueEventBundleAsync` on the durable path: try the bus; on acceptance, delete the row on a
context of its own — the delete executes at once, outside the save's transaction, which has
committed; on refusal, leave it. A drain that ran in between has already published and deleted the
row; the delete then affects nothing and the bundle was delivered twice, which at-least-once covers. A delete that fails leaves a row the drain will
publish again — at-least-once, already required of every handler — and is logged, not thrown: the
caller's save has committed and been published, and an exception now would be the after-commit
failure D1 rules out.

*Alternative considered:* defer every delete to the drain. Then the table holds every bundle until
the worker's next pass and the "common case never touches the database" sentence is false twice
per save instead of once. The immediate delete keeps the table near empty on a healthy bus.

### D4 — A replay in progress stores and does not publish, as today

`IProjectionReplayState.IsReplayActive` suppresses the publish; with durable bundles the row is
already there, so suppression means "leave it for the drain, which is itself suppressed while the
replay runs" — the existing behaviour of *Publication is suppressed while a replay is in progress*,
unchanged.

## Risks / Trade-offs

- **The outbox table gets a row per save on hosts that opt in.** → Deleted on acceptance; on a
  healthy bus the steady state is near empty. Operators who alert on outbox depth are told in the
  guide that a spike now means the bus is down, which it meant before too.
- **The estimate in D1 is an estimate.** → Task 4.1 measures with the PoC's benchmark before the
  guide calls it cheap; if the cost is above 15 % of B1's throughput the guide says so and the
  default question for 5.0 is answered with that number. *Measured 2026-09-14
  (`evidence/results.md`, C1): the cost is about 30 %, twice the bound — the estimate was wrong,
  the guide says so, and the 5.0 default stays `false`.*
- **A consumer's own dispatcher keeps bus-first silently.** → By design; the interface defaults
  say so in their XML docs, and the option is documented as honoured by the framework's dispatcher.
- **The port now carries a reference-identity contract.** → The dispatcher recognises the bundle it
  stored by instance; a decorator that clones the bundle between store and publish drops to
  bus-first for that save and leaves the stored row for the drain — a duplicate delivery, never a
  loss. The XML docs on the port say "the same bundle instance".

## Migration Plan

1. Ship with `DurableBundles = false`. No host changes behaviour.
2. A host that runs sagas on the bus path, or cannot afford a replay, sets `DurableBundles = true`
   and observes the outbox table's new steady state.
3. Rollback: set the option back; rows already stored are drained by the worker as any stored
   message.

## Open Questions

- Whether `DurableBundles = true` becomes the default in 5.0 is answered by the measurement in task
  4.1 and the owner's release decision; nothing in this change depends on the answer. *Answered
  2026-09-14 by C1: at a 30 % throughput cost it does not become the default.*
