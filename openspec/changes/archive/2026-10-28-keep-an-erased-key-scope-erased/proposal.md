# Keep an erased subject's keys erased

> **Status:** approved

## Why

An erasure shreds every key that names the subject, and nothing stops the next encryption for that
subject from creating a new one. A key store asked for the current key of a scope it does not hold
creates a first version, and it does not tell a scope that never had a key from one that was erased.
Every encryption goes through that request.

Work that is already running when the erasure begins — a job, a request in flight, a second process
sharing the key store — can therefore reach an encryption after the key material is gone. It gets a
fresh key, the data it writes under that key stays readable, and no later erasure knows the key is
there unless it is run again. The same holds for a combination that never had a key at all: an
operator acting in a tenant from outside it, writing there for the first time after the tenant was
erased, gets a key that names the erased tenant.

The erasure promises that the subject's data becomes unrecoverable. A key minted for it afterwards
breaks that promise silently, and the only place that sees every encryption is the key store.

## What Changes

The consumer-visible effect: once a subject is erased, the key store refuses to create key material
for it — for a tenant, a key of any level that names the tenant; for a user, a user-level key that
names the user, which is exactly what the erasure shreds. The refusal is a dedicated exception
carrying the scope, so a consumer can map it to its own "gone" answer.

- `IKeyStore` gains a member that marks a subject erased. It has a default implementation that throws
  `NotSupportedException`, so an existing custom key store still compiles and behaves as before.
- After a subject is marked erased, the request for a scope's current key and the rotation of a scope
  both throw the new exception for every scope that names the subject, and create nothing. A scope
  that names neither is unaffected. Revoking a version marks nothing.
- The mark is permanent. There is no way to lift it; an erased subject's identity is not used again.
- The framework's key stores implement it: the file-backed store keeps the mark in its file, so it
  survives a restart and every process sharing the file sees it; the in-memory store of the
  test-support package keeps it in memory. The development key store refuses it, as it refuses every
  other erasure operation.
- The subject eraser marks the subject before it sweeps any plane, so an encryption racing
  the erasure either lands before the mark — and its key is shredded with the rest — or is refused.
  A key store that cannot mark a subject leaves the erasure as it is today, with a warning that the
  subject's keys can be created again.
- **BREAKING (behaviour):** an encryption for an erased subject now fails where it used to succeed
  with a fresh key. Code that erased a subject and then deliberately wrote for it again sees the
  refusal.
- The file-backed store's file format moves to a new version. A store refuses a file whose version it
  does not know. **All processes sharing one key store file must be upgraded together**: a process on
  an earlier release does not know the mark and drops it the next time it writes the file.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `data-encryption`: a new requirement — an erased subject is given no new key — and *One key store
  serves several processes over shared storage* gains a scenario for the mark seen across processes
  and states that processes sharing a file run the same file format.
- `tenant-directory`: *A subject's erasure can be performed as one operation* marks the subject before
  shredding its keys, and warns where the key store cannot.
- `test-support`: *In-memory doubles honour the contracts they stand in for* — the key store double
  refuses an erased subject too.

## Impact

- `src/Stratara.Abstractions/Abstractions/Security/IKeyStore.cs` — the new member with a default
  implementation; the documentation of the current-key request and of rotation names the refusal.
- `src/Stratara.Abstractions/Abstractions/Security/KeySubject.cs`, `KeySubjectKind.cs`,
  `KeyScopeErasedException.cs` — the subject the member takes and the new exception.
- `src/Stratara.Abstractions/Abstractions/Erasure/ISubjectEraser.cs` — remarks name the mark and a store
  that cannot mark.
- `src/Stratara.Security/EnvelopeFileKeyStore.cs`, `src/Stratara.Security/KeyStoreFile.cs` — the mark,
  the refusal, file format version 2 and the refusal of an unknown version.
- `src/Stratara.Security/DummyKeyStore.cs` — refuses the new member.
- `src/Stratara.Testing/InMemoryKeyStore.cs` — the mark and the refusal.
- `src/Stratara.Infrastructure/Security/SubjectEraser.cs` — marks first, warns where it cannot.
- `src/Stratara.Diagnostics/LogEvents.cs` — the warning's event id.
- `docs/guides/encrypt-data-setup.md`, `docs/guides/tenant-membership.md`, the `Stratara.Security` and
  `Stratara.Testing` READMEs — the refusal, the upgrade-together rule, a custom key store should
  implement the mark.
- `llms-full.txt` — the reference catalogue lists the new exception.
- `CHANGELOG.md`. A behaviour change and new public types: a minor release.
