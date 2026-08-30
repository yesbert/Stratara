> **Status:** approved

# Fix type-name normalization for generic types

## Why

`TypeNameNormalization.ToVersionIndependent` truncates an assembly-qualified name at its second
comma, intending to keep "type name plus assembly name". For a **closed generic** the type-argument
brackets contain commas of their own, so the truncation cuts inside the brackets and drops the outer
assembly entirely.

The result is consistent — registration and resolution normalise the same way — so ordinary lookup
still works. The defects are that two closed generics differing only in their outer assembly collide
onto one key, and that the key is a malformed fragment. `TrustedTypeResolver.Register` then uses
`TryAdd`, so on a collision the first registration silently wins and the second is discarded.

No test covers a generic type on either path. Migration findings **EV-1** and **EV-2**.

## What Changes

- Parse the assembly-qualified name properly rather than by comma position, so the normalised key is
  the type name and assembly name in every case, including closed generics.
- Fail on a conflicting registration instead of discarding it silently.
- Tests for closed generic types on the resolver and the upcaster source-matching path.

Normalization itself needs no new requirement: `event-schema-evolution` already states what it is
*for* — matching on the type name and assembly name alone — and this change makes that true for the
case where it currently is not. Rejecting a conflicting registration is new behaviour, and it carries
a delta.

## Impact

Affected capability: `event-schema-evolution`. The requirement that only registered types are
resolvable gains the collision case: two different types claiming one recorded name is now rejected
at registration rather than resolved to whichever registered first. A host whose type discovery
produces such a clash fails at start-up instead of failing later, when a stored row cannot be read.

## Decisions

**Generic event, command and aggregate types remain supported.** They were never rejected, only
handled wrongly, and rejecting them now would break any consumer that already persists one. The
normalized name therefore keeps the type arguments rather than erasing them.

**Each type argument is reduced the same way as the outer name.** Keeping the outer assembly but
leaving `Version=` inside the brackets would trade one version dependence for another: an upgrade of
the *payload's* assembly would orphan every `Event<Payload>` row already written. Version
independence only holds if it holds for the whole name.

**The old truncation discarded too much, never too little.** It cut inside the type-argument
brackets, ahead of every `Version=` segment, so registration and resolution always agreed and
ordinary lookup worked. That is why the defect is a collision between distinct types rather than a
failed lookup, and why a test for plain version independence on a generic passes both before and
after this change.
