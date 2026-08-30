# Tasks

## Cleanup — can start immediately, independent of the fixes

- [ ] Replace every reference to the internal context directory in `openspec/` with the statement it
      carries. Roughly 43 occurrences, almost all in `## Impact` sections naming a dissolved internal
      file; the migration change's evidence directory holds most of them.
- [ ] Rewrite the five design notes that name a consumer application so the reasoning survives
      without the attribution. All five are capability backfills.
- [ ] Extend `scripts/check-public-mirror.sh` to scan `openspec/`, so the gate that catches this
      class of regression covers it from then on. Verify the guard in **both** directions before
      believing it — a scan that reports zero is also what a broken scan reports.

## Gate — before flipping the mirror

- [ ] `harden-bus-envelope-against-replay` shipped.
- [ ] `guard-development-and-test-doubles` shipped.
- [ ] `align-environment-guards` shipped.
- [ ] The downstream consumer informed privately, per `SECURITY.md`.

## The flip itself

- [ ] Add `openspec` to `TOP_LEVEL_DIRS` in `scripts/sync-to-github.sh`, and remove the comment
      explaining its absence.
- [ ] Run the sync in dry-run and confirm the final sanity scan passes on the prepped tree.
- [ ] Decide whether the DocFX site should surface the specs, or whether the repository is enough.
