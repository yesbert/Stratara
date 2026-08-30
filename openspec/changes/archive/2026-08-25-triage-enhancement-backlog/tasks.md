# Tasks

## Correct the record

- [x] **T2-5, T2-6, T2-8 — already correct in the row bodies.** Each carries its 2026-08-19 triage
      status, naming the capability, the change, or the reason. Nothing to redo.

## Record the decisions in the file itself

- [x] **T2-6 and T2-11 are marked queued, each pointing at its change; the six dropped rows each
      carry a reason.** Verified row by row rather than assumed.
- [x] **The contradiction was elsewhere: the forward-looking prose.** The per-row statuses were
      right, and two other places still said T2-5 was the *next candidate* — the status line at the
      top and the last entry of "Empfohlene Sequenz". A reader who scanned either would have
      concluded there was open work. Both corrected, and the sequence now says explicitly that it
      ends and that what comes next lives in `openspec/changes/`.

## Freeze

- [x] **The frozen-chronicle header is accurate.** Every row has an outcome and a reason, and the
      two stale forward-looking claims are gone, so nothing in the file reads as waiting for anyone.
- [x] **`phases.md` Phase 9 narrowed from eight open steps to one.** Seven were already answered —
      not by a decision but by the artefacts existing and being in use: `SUPPORT.md`,
      `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md` and `SECURITY.md` all exist,
      `build/PackageReleaseNotes.targets` settles the release-notes strategy, the tag policy is in
      `rules/core.md`, and the nuget.org identity is answered by twenty-five packages published under
      it. The one genuine remainder is the reserved-prefix application, whose own precondition
      ("after 1–2 pushes") has been met since 3.0.23. It stays in `phases.md` rather than moving to
      the change queue because it is an administrative request to nuget.org, not a behaviour change.
