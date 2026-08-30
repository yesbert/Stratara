# Tasks

## New pages (D-1)

- [x] `docs/guides/evolve-an-event-schema.md` — from the `event-schema-evolution` spec. Both
      boundaries are their own section: an upcaster receives the payload as persisted, so a
      protected field is ciphertext and can be renamed or moved but never read or derived from;
      and snapshots are never upcasted, so a stale one fails to restore rather than being
      transformed.
- [x] `docs/guides/use-resilience-policies.md` — from the `resilience` spec. Opens with the retry
      re-running the handler and the two conditions that make opting in safe, before any code.
- [x] `docs/guides/write-an-update-handler.md` — from the `update-change-tracking` spec. Leads with
      the three-way comparison and the row that matters (a stale copy does not overwrite a
      concurrent edit), then UC-2: a property missing on either side is ignored silently, so a
      renamed field stops updating with no error at all.

## Additions to existing pages

- [x] `docs/guides/write-a-projection.md` — the replay hazard (PR-1).
- [x] `docs/concepts/tamper-evident-streams.md` — the anchor interval is five events, with what it
      actually bounds spelled out: the exposed window is the un-anchored events plus the unchained
      ones plus everything since the last *external* commitment, and only the last term matters.
- [x] `docs/guides/api-keys-and-pats.md` — no last-used tracking (AK-1), and it cannot be
      reconstructed later; machine-key membership rows carry no marker (AK-2).
- [x] `docs/guides/external-login-oidc.md` — a JIT-provisioned user has an account but no
      membership, so its first session has no tenant claim and every scoped check fails closed
      (EI-2).
- [x] `src/Stratara.ServiceDefaults/README.md` — the Development log-file deletion (O-2), including
      the two ways it bites.

## Internal

- [x] **P-2, answered 2026-08-23: staleness fails the audit; per-project scoping was tried and
      rejected on evidence.** The task offered either. Scoping was attempted first and does not
      work: the exposure reaches seven projects, and four of them acquire it *transitively* through
      a `ProjectReference` on the test-support package rather than a direct `PackageReference`.
      `NuGetAuditSuppress` does not flow across a project reference, so scoping means listing every
      project that transitively touches EF Core SQLite — and the next project to use the test host
      breaks the build for a reason that has nothing to do with it. The build proved this rather
      than the reasoning: the scoped attempt failed with NU1903 on four projects the grep had not
      found. The suppression stays at the root, and `scripts/security-audit.sh` now **fails** on a
      stale allowlist entry instead of printing a note — which addresses the finding's actual
      concern, a suppression outliving its advisory. Verified against a deliberately stale entry.
