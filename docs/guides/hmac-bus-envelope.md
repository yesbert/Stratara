# Bus-Envelope Integrity (HMAC)

> **Derived page.** The behaviour described here is specified by the `bus-envelope-integrity` capability
> under `openspec/specs/`. That specification is the source; this page explains and
> illustrates it. Where the two disagree, the specification is right and this page is a bug.

Stratara supports an **opt-in HMAC signature** on every `CommandEnvelope` + `EventBundle` that travels on the bus. When enabled, consumers verify the signature before deserializing the body; tampered envelopes are rejected.

## When to turn this on

- **Multi-tenant brokers** — if your bus connects multiple apps with different trust levels (e.g. a shared RabbitMQ cluster).
- **Defense-in-depth** for compliance regimes where "message provenance" needs an auditable verifier.

You **don't** need this for single-app, single-host scenarios — the network + broker auth already gates who can publish.

## Wire it

```csharp
services.AddBusEnvelopeIntegrity(options =>
{
    options.Mode = BusEnvelopeIntegrityMode.Strict;
    options.SharedKey = Convert.FromBase64String(
        builder.Configuration["BusEnvelopeIntegrity:SharedKey"]!);
});
```

`SharedKey` is a `byte[]` of at least 32 bytes — decode it from wherever your secrets live. The
overload `AddBusEnvelopeIntegrity(configuration)` binds `Mode` from the `BusEnvelopeIntegrity`
section (`BusEnvelopeIntegrityOptions.SectionName`) and leaves the key to you.

Three modes:

| Mode | Producer behaviour | Consumer behaviour |
|---|---|---|
| `Off` (default) | No signature added | No verification — accepts any envelope |
| `Permissive` | Always signs | Verifies if a signature is present; accepts unsigned (for rolling deployments) |
| `Strict` | Always signs | Rejects unsigned envelopes + envelopes with invalid signatures |

Roll-out pattern: deploy producers with `Permissive` first, wait for the entire fleet to be running the signed version, then flip consumers to `Strict`.

**The same pattern applies when the projection itself changes**, and 3.4.0 is the first release where it does. Signatures produced by a pre-3.4.0 publisher do not verify against a 3.4.0 consumer. Move producers to 3.4.0 while consumers run `Permissive`, let the in-flight messages drain, then return consumers to `Strict`. Upgrading both sides straight into `Strict` rejects everything still in the queue.

## What's signed

Every field of the message except the signature itself:

- **CommandEnvelope**: the envelope id, the command type, the session context, the heavy-lane flag, and a digest of the command body.
- **EventBundle**: the session context, and a digest over every field of every event it carries.

Two properties of the projection are load-bearing, and both are why it looks the way it does.

**Every field is length-prefixed.** Joining fields with a separator that a field is allowed to contain would let content be shifted across a boundary without changing the projection — so a signature captured from one message could be presented with a *different command type* and still verify, defeating the guard the type name is signed for.

**The projection is built from field values, never by re-serializing.** The payloads survive the envelope's own deserialization as strings, and everything else is a scalar with a canonical text form. So a message that has been on the wire projects to exactly what its publisher signed, whatever the serializer did with property order or escaping — and a later release that serializes payloads differently does not invalidate older signatures. Verification also runs *before* upcasting, on the received bytes.

> **Changed in 3.4.0.** Until then the signature covered identity only — for an event bundle that was everything except the events. That answers *minting* a session context, which was the threat it was designed for, but not *transplanting* an observed signature onto different events: an attacker who had seen one signed message could publish arbitrary events into that tenant, and the projection and saga workers would act on them. Signatures produced before 3.4.0 do not verify against 3.4.0 and later. See the rollout below — this is the migration the three modes exist for.

## The signer interface

```csharp
public interface IBusEnvelopeSigner
{
    string Sign(string payload);
    bool Verify(string payload, string? signature);
}
```

`payload` is the canonical projection — `BusEnvelopeCanonical.Of(envelope)` produces it, and the
framework calls it for you. Treat the string as opaque bytes to sign; do not parse it.

Default impl: `HmacBusEnvelopeSigner` — HMAC-SHA-256 over the canonical projection. Constant-time compare via `CryptographicOperations.FixedTimeEquals`. Length-check happens before the compare (v3.0.13+ — protects against `ArgumentException` from missized attacker-supplied signatures).

## Startup probe

`BusEnvelopeIntegrityStartupProbe` (v3.0.13+) warns at host-start if `Mode != Off` but no signer is registered, or if `Mode == Off` on any host that is not in Development. Every host that is not your laptop should default to at least `Permissive`.

**Changed in 3.4.0**: the off-mode warning used to fire only when the environment was *named* `Production`. A host named `Production-EU` or `prod` is a production host that a name check does not recognise — and it is exactly the host whose messages travel a real broker. The warning is now governed by "not Development", which also surfaces the deviation on staging and QA.

## Configuration

```jsonc
{
  "BusEnvelopeIntegrity": {
    "Mode": "Strict"
  }
}
```

The section name is `BusEnvelopeIntegrity` — it is what `AddBusEnvelopeIntegrity(configuration)`
binds. Keep the shared key out of `appsettings.json`; read it from your secret store and assign it
in the `Action<BusEnvelopeIntegrityOptions>` overload as shown above.

Rotate the key by shipping the new key to all participants first, then redeploying. There's no built-in rolling-key support — the shared key is a single value at any point in time.

## Size and depth guards

A signature answers *who sent this*. It does not answer *how big is it* — verification happens after
the bytes are already in memory, and a hostile publisher can exhaust a consumer without ever forging
anything. Two limits apply to every inbound envelope, independently of `Mode`, and bind from the
`BusEnvelopeJson` section:

```jsonc
{
  "BusEnvelopeJson": {
    "MaxDepth": 32,
    "MaxBodyBytes": 1048576
  }
}
```

`MaxBodyBytes` (default 1_048_576 bytes — one mebibyte) is checked against the raw payload length *before* deserialisation,
so an oversized message is rejected rather than allocated. `MaxDepth` (default 32, against
`System.Text.Json`'s own 64) bounds nesting, which is what a payload built to exhaust the stack
relies on. Raise `MaxBodyBytes` if your commands legitimately carry more, and treat the raise as a
capacity decision — it is the ceiling on what one hostile message can cost you.
