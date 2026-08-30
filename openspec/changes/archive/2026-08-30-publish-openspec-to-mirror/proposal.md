> **Status:** proposed — and never implemented as written. Superseded by `make-github-the-only-repository`, 2026-08-30.

> **Outcome.** Its goal was reached, by the opposite route to the one planned here. Rather than
> extending the mirror's allowlist to carry `openspec/`, the mirror itself was removed: GitHub became
> the only repository, so everything in it is public by construction. Of the four preconditions,
> two were met on their own — the three security changes shipped in `3.4.0` and the consumer was
> coordinated with privately before that tag — and two were carried out by the superseding change:
> the internal references were resolved into the statements they carried, and the design notes that
> named consumer applications went with the pre-migration archive into the private context
> repository. The tasks below are left as they were written, including the ones about a sync script
> that no longer exists; rewriting them would hide which plan was actually followed.

# Publish `openspec/` to the public mirror — after the cleanup and the fixes

## Why

`openspec/` is deliberately **not** in the public GitHub mirror. That is a decision taken on
2026-08-19, not an oversight, and this change records it together with what has to be true before it
is reversed.

The intent is to publish **both** the specs and the changes — the specs because they are the
framework's actual contract, and the changes because the decision records were dissolved into the
archive, so an internal-only archive would mean the rationale was deleted rather than moved. The
question was never *whether*, only *when*.

Three things stand in the way, and two of them are mechanical rather than editorial:

1. **The mirror sync would fail.** `scripts/sync-to-github.sh` scans the whole prepped tree for
   references to the internal context directory and aborts if it finds any. `openspec/changes/`
   currently holds about 43, almost all of the form "this internal file was dissolved into this
   change". Pipeline 40 would not produce an ugly mirror — it would not run.
2. **Five archived design notes name the consumers.** They describe how two consumer applications
   had built validation and key management internally, including the defects that prompted the first
   two decision records. That violates the project's own rule against consumer-app references in
   public files, and it does so in substance rather than in form.
3. **The open queue names unfixed defects.** `SECURITY.md` promises that *"severe issues are
   coordinated with downstream consumers on a private channel before public disclosure"*. Publishing
   `harden-bus-envelope-against-replay` — which describes an exploitable weakness in a mechanism
   sold as a security feature, in enough detail to act on — before it is fixed contradicts that
   promise, with the adopting application as exactly the downstream consumer it names.

## What Changes

Nothing yet. This change is the record of the decision and the gate on reversing it.

When the preconditions below are met: add `openspec` to the mirror's `TOP_LEVEL_DIRS` allowlist and
extend `scripts/check-public-mirror.sh` to cover it, so the same gate that protects `docs/` protects
the specs.

## Preconditions

- [ ] The references to the internal context directory in `openspec/` are removed — replaced by the
      statement they carry, not by another link. This is the option the public-mirror rule itself
      recommends.
- [ ] The five consumer-naming design notes are rewritten to describe the *situation* without naming
      the consumers. The reasoning survives; the attribution does not need to.
- [ ] The security-relevant changes are fixed and released:
      `harden-bus-envelope-against-replay`, `guard-development-and-test-doubles`,
      `align-environment-guards`.
- [ ] The downstream consumer is informed of the three above through the private channel
      `SECURITY.md` promises, before any of it becomes public.

## Impact

Until this change ships, `openspec/` stays internal. The mirror excludes it by construction — the
sync script uses an allowlist of top-level directories and `openspec` is not on it — so no action is
needed to keep it out, and the allowlist now carries a comment saying the absence is deliberate.
