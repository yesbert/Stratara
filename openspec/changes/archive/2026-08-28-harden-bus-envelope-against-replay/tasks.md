# Tasks

## Decision

- [x] **Owner decision, 2026-08-19: yes.** Signature transplant is in scope; the canonical form gains
      a digest of the payload. The alternative — stating the limit and closing the change — was
      considered and rejected, because the fix is cheap enough that the limit would have been a
      saving of nothing.

- [x] **Owner decision, 2026-08-28: close the field-separator ambiguity in the same change.** The
      canonical form joins fields with `|`, and both `CommandTypeName` and `SessionContextJson` may
      contain one. Content can therefore be shifted across a field boundary without changing the
      canonical string — which changes *which command type is dispatched* while the signature still
      verifies, defeating the type-confusion guard that `CommandTypeName` is signed for. Not raised
      by the proposal; found while writing the new form. Each field is now length-prefixed. Taken
      here rather than later because this change already breaks signatures and already plans the
      permissive rollout; deferring would force a second one.

## Implementation

- [x] Extend `BusEnvelopeCanonical.Of` for both shapes to include a digest of the payload
      (`src/Stratara.Abstractions/Messaging/BusEnvelopeCanonical.cs`). Bundle: over the serialized
      events. Command: over `CommandJson`, plus the `Id`, which is already transmitted and currently
      unsigned.
- [x] Build the digest input as a **defined concatenation of field values**, not by re-serializing the
      deserialized object. This is what makes the signature stable: `EventMessage.DataJson` and
      `CommandEnvelope.CommandJson` are `string` fields that survive the envelope's own
      deserialization verbatim, and the remaining fields are scalars with canonical text forms. A
      re-serialization would not be guaranteed to reproduce the publisher's bytes (property order,
      escaping, culture), and the failure would be intermittent rather than immediate — the worst
      possible shape for a signature check.
- [x] Add a guard test that **fails when `EventMessage` or `CommandEnvelope` gains a field**. A field
      added later and not added to the canonical form is silently unsigned, and nothing else would
      catch it. Reflect over the record's primary-constructor parameters and assert the expected set.

## Tests

- [x] Transplant is refused: same session context, different events → verification fails.
- [x] Transplant is refused on the command path: same type and session, different `CommandJson`.
- [x] The existing session-tamper and type-tamper tests still pass unchanged.
- [x] A message whose payload serialization differs from the current release still verifies, so the
      independence-from-schema claim is pinned rather than asserted.

## Rollout

- [x] Migration note: signatures produced by an older publisher stop verifying. Fleets move through
      **permissive** mode — publishers upgrade first, consumers follow, then strict is safe again.
      This is the rollout the three modes were designed for; it is the first time it is actually used.
- [ ] **Owner action, not an agent action.** Coordinate with the downstream consumer privately
      before any of this becomes public, per `SECURITY.md`. This
      change is one of the three gating `publish-openspec-to-mirror`.

## Carried in from `align-environment-guards`

- [x] `BusEnvelopeIntegrityStartupProbe` warned only when `IsProduction()`, so a host named
      `Production-EU` or `prod` — a production host by every measure except the name — got no warning
      that its bus envelopes were unsigned. Inverted to "not development", which also warns on staging
      and QA, and the `bus-envelope-integrity` requirement now says the warning is governed by whether
      the host is in development rather than by whether it is named production. Found by the
      systematic `IsProduction()` sweep that change's task list required; recorded there and carried
      here because it belongs to this capability.
