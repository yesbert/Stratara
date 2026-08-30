# Tasks

## Decision

- [x] **Ordering, decided 2026-08-23: API keys, settings, memberships, key material — and the
      memberships are *read* first.** Key material last is the obvious half: shredding it first
      makes every other sweep operate on rows nobody can read, so a later failure would leave data
      that cannot be identified, let alone resumed. API keys first is the other half: a credential
      that still works while the erasure runs can act on the subject's behalf mid-sweep. The part
      the hint did not anticipate is the read: a user's settings and keys live per tenant, and the
      only thing that knows which tenants is the membership store — so memberships must be read
      before they are removed, and removed before the key material they point at.
- [x] **Home, decided 2026-08-23: contract in Tier-A, implementation in `Stratara.Infrastructure`.**
      All four swept contracts already live in `Stratara.Abstractions`, so no tier is inverted by
      depending on them together. `ISubjectEraser`, its report and its exception go there too, so a
      consumer can implement or catch without taking a dependency on infrastructure — the same rule
      that puts every other SPI in Tier-A. The composition itself is composition glue, which is what
      `Stratara.Infrastructure` is for.
- [x] **Cross-check, answered 2026-08-23: the command log and the outbox are out of scope, stated
      rather than silently skipped.** Both carry a session context naming the subject, so the
      question is real. The command audit log is the evidence that the erasure happened and who
      asked for it; erasing it destroys the trail that proves compliance. Whether an audit record is
      retained under a separate lawful basis is a decision only the consumer can take for their
      jurisdiction — the framework must not take it for them. The outbox is transient: an entry
      outliving an erasure means a stuck dispatcher, which is an operational fault, not an erasure
      gap. Both are named in the documented non-coverage rather than left for a reader to discover.

## Implementation

- [x] `ISubjectEraser`, `ErasureReport`, `ErasedPlane`, `ErasurePlane` and
      `ErasureIncompleteException` in `Stratara.Abstractions`.
- [x] `SubjectEraser` in `Stratara.Infrastructure`, sweeping the four planes in the decided order
      and reporting the scopes each one covered.
- [x] Stop at the first failing plane rather than continuing. This is the property that makes the
      ordering worth anything: a failed settings sweep must never be followed by the key shred.
- [x] `AddStrataraErasure()` registers it. It adds no store of its own — the four it sweeps are the
      consumer's to register.
- [x] Document the boundary in the derived page: consumer-built read models, unprotected stream
      data, the command log, the outbox, and system-wide key material.

## Tests

- [x] User erasure sweeps all four planes, in order, and clears the active-tenant selection with the
      memberships.
- [x] User erasure covers every tenant the user belongs to — three setting scopes and three key
      scopes for a user in two tenants.
- [x] User erasure leaves other subjects untouched.
- [x] Tenant erasure sweeps all four planes and leaves the same user's data in another tenant alone.
- [x] A failing plane raises `ErasureIncompleteException` naming the plane and listing what was
      already swept.
- [x] A failing plane leaves key material intact. This is the test that would catch a future
      refactor reordering the sweeps.
