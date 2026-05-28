# Security Policy

## Supported versions

| Version | Supported          |
|---------|--------------------|
| `3.x`   | :white_check_mark: |
| `2.0.x` | maintenance — security fixes only |
| `1.x`   | :x: end-of-life    |

`preview` builds (`{version}-preview.{BuildId}`) are not supported. Production deployments must pin to a released tag (`v*`).

## Reporting a vulnerability

If you believe you have found a security issue in Stratara, **please do not open a public issue.** Send a private report to:

> **security@stratara.tech**

Include enough detail to reproduce: package + version, the affected API surface, a minimal repro, and the expected vs. observed behaviour. Encrypt with our public key if the disclosure is sensitive (PGP key on request).

We aim to acknowledge within **5 business days** and ship a fix or an explicit "won't fix" decision within **30 days** for actionable reports. Severe issues are coordinated with downstream consumers on a private channel before public disclosure.

## Out of scope

- Vulnerabilities in third-party dependencies (report to the respective project)
- Issues that require physical access to the host running Stratara
- Self-XSS, CSRF on local-only endpoints, and other findings that depend on the consumer's hosting configuration rather than the framework code
- Findings against `preview` builds (treat preview as untrusted)

## Trust boundaries

### Message bus

Stratara routes commands and events through an `IMessageBus` implementation (RabbitMQ, Azure Service Bus). The bus itself is treated as a trusted transport: any party with publish credentials can place arbitrary `CommandEnvelope` and `EventBundle` messages onto the topic.

Each envelope carries a `SessionContextJson` field that drives the consumer-side `ISessionContextProvider.Set(...)` call and the AAD used for AES-GCM decryption. A hostile publisher who can mint envelopes with attacker-chosen `TenantId` / `ActorTenantId` / `ActorUserId` values can therefore impersonate any tenant — the framework treats the field as trusted by default.

**Mitigation (opt-in).** Call `services.AddBusEnvelopeIntegrity(o => { o.SharedKey = …; o.Mode = BusEnvelopeIntegrityMode.Strict; })` on every publisher and consumer that share a bus. The framework then HMAC-SHA256-signs the trust-relevant slice of each outbound envelope (`CommandTypeName + "|" + SessionContextJson` for `CommandEnvelope`; `SessionContextJson` for `EventBundle`) and verifies it on the receiving side. See `BusEnvelopeIntegrityOptions` for the `Off` / `Permissive` / `Strict` enforcement modes — `Permissive` is the recommended rolling-deployment step before flipping the fleet to `Strict`.

**Threat model when integrity is `Off`.** The framework is safe against the default attack on encryption (a hostile publisher cannot read encrypted payloads), but is *not* safe against impersonation by a publisher who tampers with `SessionContextJson`. Treat publish credentials with the same scrutiny as application-tier secrets.
