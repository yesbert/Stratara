# Concepts

The three load-bearing ideas behind Stratara. Read these to understand *why* the framework exists, not just *what* it ships.

- **[Tamper-Evident Streams](tamper-evident-streams.md)** — every event chained into the next via SHA-256, so a direct-DB mutation shows up the moment you recompute the chain. Periodic anchors can be committed to an external source of truth (a public chain, a notary, a timestamp authority) when you need to defend against an insider who controls the whole database — no blockchain consensus tax for the common case, an escalation path for the hard one.
- **[Tenant-Aware Encryption](tenant-aware-encryption.md)** — AES-GCM with an authentication tag bound to the tenant id. Cross-tenant decryption fails by cryptography, not by query filtering.
- **[Why Event Sourcing](why-event-sourcing.md)** — what append-only event streams give you that update-in-place storage cannot: replay, time-travel, new projections from old history, a real audit trail.

Each concept has a runnable hero sample under [`samples/`](https://github.com/yesbert/Stratara/tree/main/samples) — short, zero-dependency console apps that demonstrate the idea in ~150 lines.
