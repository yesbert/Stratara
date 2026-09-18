# Design

## D1 — Where a heavy unit runs

**Decision.** The pool grain keeps acceptance, the lease and the dedupe; the unit runs on a silo-wide
runner with `HeavyWorkGrain.MaxLocalWorkers` (eight) workers, and the permit is taken on the worker.

**Why not the stateless worker again.** Eight activations per silo gave real parallelism, but a
stateless worker activates on the silo of its caller. The caller is the drain or the dispatcher, which
may sit on a silo that registered no command role and therefore has no handlers — the failure
`close-the-round-5-execution-gaps…` fixed by giving the pool a placement filter. A placement filter and
a stateless worker do not combine: the stateless placement director activates locally and never looks
at the filtered silo list.

**Why not `Task.Run`.** The repository forbids `Thread`, `new Task(...)` and `Task.Run` in framework
code. The runner uses the shape `IntentCompletionQueue` already uses: an `IHostedService` whose
`StartAsync` starts loops over a `Channel<T>`, each awaiting the channel and then invoking the unit.
The loops run on the thread pool because a hosted service has no synchronization context, and the
unit's delegate is invoked by the loop, so `TaskScheduler.Current` inside the handler is the default
one — not the activation's.

**What stays on the grain's scheduler.** The grain awaits the runner's completion, so the state it
keeps — the held intent ids and the units in flight for the deactivation wait — is only ever touched
in a turn. The continuation after the unit returns is posted to the activation, as every `await` in a
grain is.

**The lease starts before the queue.** The grain creates the scope and starts the intent's lease
before it hands the unit to the runner, so a unit that waits for a worker is leased while it waits —
the property `HeavyBurstTests` covers. Only the handler and the permit move off the grain.

**Consequence for the bound per silo.** Eight workers per silo, whatever number of pools the
cluster-wide limit needs; before this change it was eight slots per pool activation, which never ran
more than one unit anyway. The cluster-wide bound stays the permits'.

## D2 — A refused running unit keeps its claim on the bound

**Decision.** `PermitLedger` remembers a refused reclaimer — unit id, holder, and an expiry one lease
away — and counts open reservations against the limit in `TryAcquire`. A reclaim that succeeds clears
the reservation; a reservation is refreshed by every further refusal, so it lives as long as the unit
keeps asking and expires a lease after the unit stopped asking (it ended, or its silo died).

**Why not give the running unit its permit unconditionally.** That would exceed the bound by design,
which is what the permits exist to prevent. Reserving instead means the bound is exceeded only until
the next unit ends, which is the outcome
`keep-the-heavy-work-bound-across-a-permit-owner-failover` intended.

**Why the ledger and not the worker.** The worker cannot know whether a permit is free; only the
ledger can, and it is a single activation, so the reservation needs no synchronisation.

## D3 — What the tests have to show

- Two heavy handlers that compute without yielding, on one silo, run beside each other and both
  count as running: neither is handed over twice, and both complete once. An integration test with
  two units of five seconds under a two-second grace fails before this change (the second unit is not
  accepted until the first ends) and passes after it.
- A unit refused by the ledger takes the next freed permit ahead of a waiting one — a ledger unit test,
  because it is a rule of the table and not of the cluster.
- `PermitKeeperFailoverTests`' decorative assertion (a further unit starts at least a lease after the
  keeper answers again, which the 120-second units make trivially true) goes, and the test says what
  does measure the grace: no sample above the bound, and the running units counted again within a lease.
  Staging a unit that ends inside the grace would prove more and would turn a kill test into a race.
