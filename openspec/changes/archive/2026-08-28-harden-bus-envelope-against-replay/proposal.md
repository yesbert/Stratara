> **Status:** approved

# Sign the payload, not only the identity claims

## Why

A bus-envelope signature covers the message's *canonical form*, which is:

```csharp
Of(CommandEnvelope) => CommandTypeName + "|" + SessionContextJson
Of(EventBundle)     => SessionContextJson
```

For an event bundle that is **everything except the events** — the record has exactly three fields
and one of them is the signature. The payload is not covered on either path.

### The mechanism is correct for the threat it was built for

SR2-Sec-001 states that threat precisely: *anyone with publish rights can set an arbitrary
`TenantId` / `UserId` / `ActorTenantId` / `ActorUserId`, bypassing tenant isolation.* That is the
threat of **minting** a session context, and signing the session context answers it exactly.

### What was never considered

**Transplanting** an observed signature onto different events. Not decided against — never raised.
The backfill's design note asserted a rationale for excluding the payload; that rationale was
inferred rather than sourced, and it was also false. It has been corrected in place in the archived
change.

## The attack, stated precisely

It is **not** "replay the same message". That is harmless by design: delivery is at-least-once, so
handlers must already be idempotent, and a duplicate bundle is an ordinary occurrence. The word
"replay" makes this sound like the benign case; it is a different thing.

It is **signature transplant**:

1. The attacker observes one signed message. Its `SessionContextJson` contains a per-request-unique
   correlation identifier, so they cannot construct a session context — they can only reuse one
   verbatim.
2. They publish a **new** message carrying that exact `SessionContextJson` and **arbitrary events**.
3. The canonical form is unchanged, so the signature verifies. Strict mode accepts it.

What that buys the attacker:

- **They cannot reach a tenant they have never observed.** This is a real limit and it is worth
  stating — the mechanism is not worthless.
- **They can inject arbitrary events into any tenant they have observed one message from.** The
  projection worker writes them into that tenant's read models; the saga worker reacts to them and
  can issue commands as that actor.

Whether observation is realistic is answered by SR2-Sec-001's own threat list: a compromised
internal service, leaked broker credentials, a broker administrator. Two of those three plausibly
carry read access as well as publish access. The attacker the mechanism was designed to stop is
generally in a position to do this.

## Decision

**Owner decision, 2026-08-19: yes, signature transplant is in scope.** The mechanism's own threat
model is the actor who can perform it, and the fix is cheap. "Out of scope" is defensible when a fix
is expensive; it is not defensible here.

The rejected alternative was to state the limit explicitly instead — the way the tamper-evidence
capability names the attacks it does not catch. Honest, and a posture this project has taken before,
but it would have left a security mechanism delivering materially less than a reader expects for no
saving.

## The fix

**Include a digest of the payload in the canonical form.** Bundle: a digest over the serialized
events. Command: a digest over `CommandJson`, and while there, the `Id` — which the envelope already
carries and already leaves unsigned.

Why this is cheaper than it looks, and cheaper than what finding BI-1 originally suggested:

| Concern | Answer |
|---|---|
| Wire-format change? | **None.** Every field that needs covering is already on the record and already transmitted |
| Does it need re-serialization? | **No — and this is the load-bearing detail.** The payload survives the envelope's own deserialization *as a string*: `EventMessage.DataJson` and `CommandEnvelope.CommandJson` are `string` fields, and the remaining fields are scalars with canonical text forms. So the digest is taken over a **defined concatenation of field values**, exactly the technique `BusEnvelopeCanonical` already uses with its `\|` separator — never over a re-serialized object, whose byte form would not be guaranteed to match what the publisher signed |
| Breaks messages in flight? | **No.** The publisher and the receiver build the same canonical string from the same field values |
| Breaks on a serialization change? | **No.** Only messages produced after the change are affected, and they are signed with the new bytes |
| Breaks under upcasting? | **No.** Verification runs before deserialization and before `MapToEventsAsync`, on the raw received strings |
| Rollout | Signatures change, so publishers and consumers disagree during a deployment. That is exactly what **permissive** mode exists for |

BI-1 proposed binding "a message identity or a timestamp". Both are worse answers, and one is
impossible: `EventBundle` has no identifier to bind, and binding one would not help anyway, because
the events still would not be covered. A freshness window bounds transplant rather than preventing
it, and needs clock agreement between hosts.

## Impact

`openspec/specs/bus-envelope-integrity/spec.md` — the requirement covering what is signed. The delta
below assumes the answer is yes; if it is no, it is replaced by a scenario stating the limit, in the
style the tamper-evidence spec uses.
