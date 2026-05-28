# Concepts

The three load-bearing ideas behind Stratara. Read these to understand *why* the framework exists, not just *what* it ships.

- **[Tamper-Evident Streams](tamper-evident-streams.md)** — every event chained into the next via SHA-256, so direct-DB mutation is caught by the next verifier pass. Audit-grade integrity without a blockchain consensus tax.
- **[Tenant-Aware Encryption](tenant-aware-encryption.md)** — AES-GCM with an authentication tag bound to the tenant id. Cross-tenant decryption fails by cryptography, not by query filtering.
- **[Why Event Sourcing](why-event-sourcing.md)** — what append-only event streams give you that update-in-place storage cannot: replay, time-travel, new projections from old history, a real audit trail.

Each concept has a runnable hero sample under [`samples/`](https://github.com/yesbert/Stratara/tree/main/samples) — short, zero-dependency console apps that demonstrate the idea in ~150 lines.
