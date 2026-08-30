> **Status:** approved

# Stop development and test doubles from running as if they were real

## Why

Two related gaps, both of which fail by succeeding.

**The development key store simulates erasure.** Its `RevokeAsync` and `EraseScopeAsync` complete
successfully and shred nothing. A consumer exercising an erasure path in development gets a green
result and no shredding — the exact defect the framework rejected in a consumer's derivation-based
key store when it wrote ADR-0002, reproduced in its own development stub. Finding **E-1**.

**The test-support packages have no guard at all.** `Stratara.Testing` ships an in-memory key store
and an in-memory bus as hand-constructed doubles; `Stratara.Testing.EntityFrameworkCore` wires an
in-memory SQLite store, those doubles and a recording dispatcher into a single composition. All of
it would start successfully in a production host and lose every write. The framework guards the
development key store against non-development environments; the test-support packages, which are
strictly worse to ship, are unguarded. Finding **TS-1**.

## What Changes

- The development key store's erasure operations fail rather than reporting success.
- The test-support event-store composition refuses to register when the host it is being wired
  into says it is not Development. It deliberately does **not** copy the development key store's
  whitelist — the decision and its reasoning are in `tasks.md`.
- The test-support packages gain a build-time check that fails a build referencing them from a
  project that is not a test project.
- The `data-encryption` requirement covering the development store gains a scenario stating that
  its erasure paths are not simulated.
- The `test-support` requirement *Test-support packages are for test projects* changes: its second
  scenario currently records that nothing enforces the boundary, and after this change something
  does.
