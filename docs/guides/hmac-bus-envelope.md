# Bus-Envelope Integrity (HMAC)

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
    options.SharedKey = builder.Configuration["BusIntegrity:SharedKey"];
});
```

Three modes:

| Mode | Producer behaviour | Consumer behaviour |
|---|---|---|
| `Off` (default) | No signature added | No verification — accepts any envelope |
| `Permissive` | Always signs | Verifies if a signature is present; accepts unsigned (for rolling deployments) |
| `Strict` | Always signs | Rejects unsigned envelopes + envelopes with invalid signatures |

Roll-out pattern: deploy producers with `Permissive` first, wait for the entire fleet to be running the signed version, then flip consumers to `Strict`.

## What's signed

Identity-only — the signature covers:

- **CommandEnvelope**: `CommandTypeName + "|" + SessionContextJson` (the routing identity + the actor/subject context).
- **EventBundle**: `SessionContextJson` (the actor/subject context for the originating command).

**The payload body is NOT signed.** Payload tamper-resistance comes from the `[EncryptData]`-AAD binding instead (see [Encrypt Sensitive Data](encrypt-data-setup.md)) — the AAD encodes the tenant, so a tampered envelope addressed to a different tenant fails decryption.

This is intentional: keeping the signature scope small means the signing cost stays constant + the verifier doesn't have to deserialize the body to check the signature.

## The signer interface

```csharp
public interface IBusEnvelopeSigner
{
    string Sign(BusEnvelopeCanonical canonical);
    bool Verify(BusEnvelopeCanonical canonical, string signature);
}
```

Default impl: `HmacBusEnvelopeSigner` — HMAC-SHA-256 over the canonical projection. Constant-time compare via `CryptographicOperations.FixedTimeEquals`. Length-check happens before the compare (v3.0.13+ — protects against `ArgumentException` from missized attacker-supplied signatures).

## Startup probe

`BusEnvelopeIntegrityStartupProbe` (v3.0.13+) warns at host-start if `Mode != Off` but no signer is registered, or if `Mode == Off` and `IsProduction()` returns true. Production hosts should default to at least `Permissive`.

## Configuration

```jsonc
{
  "BusIntegrity": {
    "Mode": "Strict",
    "SharedKey": "base64-encoded 32-byte HMAC key"
  }
}
```

Rotate the key by shipping the new key to all participants first, then redeploying. There's no built-in rolling-key support — the shared key is a single value at any point in time.
