# Tasks

## Decision

- [x] **Shape, decided 2026-08-23: one extension method on the transaction, for the conflict case
      only.** The task set the criterion — keep the call site readable, and a helper that obscures
      the hand-written form is not an improvement. Measured against it, the two patterns come out
      differently. The "row is gone, return" pattern is already the clearest possible C#; every
      wrapper considered for it (a load-mutate-save callback trio) read worse than the two lines it
      replaced, so it is documented rather than wrapped. The conflict pattern is the one that is
      subtle, that the framework itself got *wrong* five times, and that carries the distinction a
      naive implementation loses — so that is what ships as
      `ITransaction.SaveChangesIdempotentAsync`.
- [x] **The conflict distinction is a probe, not a filter.** On conflict the helper asks the caller
      whether the write's target still exists. Gone means satisfied; still there means a real
      conflict and the original exception is rethrown with its stack intact. A C# exception filter
      would have been more elegant but cannot contain `await`.

## Implementation

- [x] `ProjectionTransactionExtensions.SaveChangesIdempotentAsync` in `Stratara.Projections`, taking
      the existence probe and rethrowing when the target survives.
- [x] Rewrite `TenantProjection` to use it — both delete handlers.
- [x] **Acceptance check (task 3's own test): does the framework's projection read better?** The
      `TenantDeleted` handler went from thirteen lines with a four-line explanatory comment to two,
      *and* it now makes the gone-versus-exists distinction it previously did not — the old code
      swallowed every conflict, which is exactly the defect this change warns about. The
      `CustomerTenantsDeleted` handler needs an any-of-these-exist probe, which was inline and read
      badly; it is a named private method now, so the call site is one line like the other.

## Tests

- [x] A commit without a conflict returns its row count and never runs the probe.
- [x] A conflict on a vanished row is satisfied and returns zero.
- [x] A conflict on a row that still exists is **not** suppressed — the negative case, and the one
      that matters most.
- [x] The probe runs exactly once, and only after a conflict.
- [x] A failure that is not a concurrency conflict passes through untouched.
- [x] The existing `TenantProjection` suite still passes against the rewritten handlers.

## Documentation

- [x] `docs/guides/write-a-projection.md` — the two races, why only one needs a helper, and why the
      probe rather than a broad catch.
