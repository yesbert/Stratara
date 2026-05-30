# Tenant-Aware Encryption

## The problem

Multi-tenant applications usually defend tenant separation with **query filtering**: every read attaches `WHERE TenantId = @currentTenant`. It works for normal paths. It is also a *soft* fence:

- A migration script with the predicate forgotten.
- A backup restored into the wrong environment.
- An ad-hoc `SELECT *` through the wrong session.
- A developer's curl-against-staging trick that lifts a row out of context.

In any of those scenarios, a tenant A row becomes readable from a tenant B context. Query filtering alone has no answer.

## How Stratara solves it

Sensitive fields marked with `[EncryptData]` are sealed at the serialization boundary with **AES-256-GCM**. The authentication tag is bound to the current `TenantId` as Associated Data (AAD):

```
ciphertext, tag = AES-GCM-Encrypt(
    key:        per-tenant-derived key from IKeyStore,
    nonce:      random 96 bits per seal,
    plaintext:  the value of the [EncryptData] property,
    aad:        SessionContext.TenantId as bytes
)
```

Decryption requires the **same** AAD. If a sealed value from tenant A is presented for decryption under tenant B's `SessionContext`, AES-GCM rejects the authentication tag with `AuthenticationTagMismatchException`. This holds **even with the correct master key**. The tenant binding is in the cryptography, not in the query.

## Use cases

### 🗑️ GDPR Article 17 — Right to Be Forgotten via crypto-shredding

The hardest part of GDPR erasure is not the live database. It's the seven years of nightly backup tapes, the cold-storage snapshots, the replicated read-replicas, the audit-log archives — all the places a row was *legitimately* copied to before the user asked to be forgotten. Physically deleting from every one is operationally impossible.

Crypto-shredding sidesteps the problem: encrypt the personal data with a per-tenant (or, in extended setups, per-subject) key, and when erasure is required, **destroy the key**. The ciphertext remains in every copy — but mathematically, without the key, it's noise. Article 17 compliance becomes a single operation against the key store, not a coordinated purge across every storage tier.

This works because the AES-256-GCM construction guarantees that decryption without the key is computationally infeasible — not "hard" in a brute-force sense, but provably outside any plausible attacker's budget.

### 🏢 Multi-tenant SaaS offboarding

A B2B customer terminates their contract. Their data needs to be unrecoverable from your systems, including from any backup or replica. With per-tenant keys, this is one operation: revoke the tenant's key in `IKeyStore`. Every live row, every backup, every cold-storage tape becomes unreadable in the same instant. No coordinated cleanup, no per-table purge scripts, no risk of missing a forgotten replica.

### 🛡️ Backup and cold-storage leak defense

A leaked DB dump, a misconfigured S3 bucket containing backups, a lost tape from a courier — Stratara's threat model treats these as expected, not exceptional. Backups carry ciphertext only; keys live in the `IKeyStore` (Key Vault, KMS, HSM), which is not in the backup. The leaked bytes are useless to the recipient without separate access to the key store.

### 📋 Compliance posture (SOC 2 / ISO 27001 / HIPAA / PCI-DSS)

Auditors repeatedly ask the same question: *how is tenant separation enforced?* "We use a `WHERE TenantId = @currentTenant` filter on every query" is a *procedural* answer that the auditor will dig into. "Tenant separation is enforced cryptographically: each tenant's data is encrypted with a key bound to the tenant id, and cross-tenant decryption fails at the AES-GCM layer regardless of query path" is a *constructive* answer that closes the audit thread. The same architecture supports HIPAA's "data at rest encryption" rule (164.312(a)(2)(iv)) and PCI-DSS Requirement 3 by default.

### 🕵️ Insider-threat reduction

DBAs, support engineers, and anyone else with direct database read access see ciphertext bytes for `[EncryptData]` fields, not plaintext. Stratara separates "can access the data store" from "can access the keys" — two permissions, two audit trails, two different people if your governance demands it.

## What it catches

| Threat | Caught by |
|---|---|
| Row moved between tenants by misconfigured backup restore | AAD mismatch on first read attempt. |
| Migration script forgot the `WHERE TenantId` filter | Returned rows are unreadable in the wrong tenant context. |
| Bug in query layer exposes another tenant's row | Same — opaque ciphertext, can't be decrypted. |
| Developer runs `SELECT *` from a wrong-tenant connection | Sees ciphertext bytes, not the plaintext. |

## What it does NOT catch

- **Compromise of the framework's running process.** If an attacker has code execution inside your app with access to the resolved `IKeyStore`, they can decrypt under any tenant context they can construct. This is the standard cryptography threat-model boundary: cryptography defends data, not running code.
- **Plaintext exposure before encryption.** `[EncryptData]` seals at the serialization boundary. If you log a property before it's serialized, the log line has the plaintext. Don't log sensitive fields.
- **Information leak through ciphertext length.** AES-GCM is a stream cipher mode — ciphertext length equals plaintext length. An observer who sees the row size can infer the field's length. Pad to a fixed length if that's a meaningful concern.

## Why AES-GCM specifically

- **Authenticated encryption.** Modes like CBC encrypt without authenticating — a flipped ciphertext bit decrypts to a flipped plaintext bit, undetected. GCM detects any tampering with the ciphertext or the AAD.
- **AAD support.** The whole point of this design is to bind a tenant id to the ciphertext. AAD is exactly that primitive — authenticated but not encrypted, validated on decryption.
- **Hardware support.** Every modern x86 CPU has AES-NI; ARMv8-A has dedicated AES instructions. Throughput is multi-GB/s per core. The bottleneck is the database, not the cipher.

## How key derivation works

`AddSecurity()` registers `IKeyStore` — by default `DummyKeyStore` (Development only, fails-fast in any other environment). Production hosts register a real implementation **before** `AddSecurity()`: the built-in `EnvelopeFileKeyStore` from the dependency-light `Stratara.Security` package (`AddStrataraFileKeyStore(configuration)`), or an Azure Key Vault / AWS KMS / HSM store.

Keys are addressed by a **`KeyScope`** — a `DataSensitivityLevel` optionally narrowed to a tenant and/or user. For each scope, Stratara resolves a versioned data-encryption key (DEK); the `EnvelopeFileKeyStore` keeps each DEK **KEK-wrapped** (never plaintext at rest) and versioned, so rotation keeps older ciphertext readable while `RevokeAsync` / `EraseScopeAsync` crypto-shred. The per-scope DEK is the actual AES-GCM key passed to the cipher — belt-and-suspenders separation on top of the AAD binding: even if AES-GCM had a flaw, per-scope keys would still isolate damage.

## Why it runs at the serialization boundary

Two reasons:

1. **In-memory is plaintext.** Domain code reads `customer.SocialSecurityNumber` and gets the plaintext. No `await Decrypt(...)` everywhere — the encryption is transparent to the handler.
2. **Storage and transport carry ciphertext.** When Stratara writes the event to the store *or* publishes it on the bus, the seal happens automatically. A consumer without key access sees ciphertext bytes — by design.

## See it in action

The hero sample at [`Stratara.Sample.Encryption`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.Encryption) shows the AAD trick directly: same SSN, two tenants, two unrelated ciphertexts, cross-tenant decryption fails with `AuthenticationTagMismatchException`. About 120 lines, zero external dependencies, runs in under a second.

## See also

- **[Encrypt Sensitive Data](../guides/encrypt-data-setup.md)** — the wire-it-up-in-your-own-host guide. How to register `IKeyStore`, mark properties, deal with key rotation, and what `EncryptionMetadataDriftGuard` catches at host-start.
- **[HMAC Bus Envelope](../guides/hmac-bus-envelope.md)** — the related integrity layer for events in flight, separate from at-rest encryption.
