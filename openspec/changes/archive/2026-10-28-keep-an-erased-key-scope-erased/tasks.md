## 1. Reproduce first

- [x] 1.1 `tests/Stratara.Security.Tests/EnvelopeFileKeyStoreTests.cs` and
  `tests/Stratara.Testing.Tests/InMemoryKeyStoreTests.cs`: after a scope is erased,
  `GetOrCreateCurrentKeyAsync` for it returns a fresh `v1`. Confirm it passes on `main` — the evidence
  of the gap — and invert it once 2.x lands.
- [x] 1.2 `tests/Stratara.Infrastructure.Tests/Security/SubjectEraserKeyCoverageTests.cs`: a tenant is
  erased, then a value is encrypted under (a) a scope that held a key before the erasure and (b) a
  scope naming the tenant and a user who never wrote in it. Confirm both succeed with a fresh key on
  `main`.

## 2. The contract

- [x] 2.1 `src/Stratara.Abstractions/Abstractions/Security/KeySubject.cs`: `readonly record struct` with
  `Tenant(string)` / `User(string)` factories and `Covers(KeyScope)` (tenant: any level, `TenantId`
  matches; user: `UserScoped`, `UserId` matches; ids `OrdinalIgnoreCase`). Unit tests for every level ×
  match / no-match / case in a new `tests/Stratara.Security.Tests/KeySubjectTests.cs`.
- [x] 2.2 `src/Stratara.Abstractions/Abstractions/Security/KeyScopeErasedException.cs`:
  `: InvalidOperationException`, carries `Scope`, message names the scope.
- [x] 2.3 `IKeyStore.MarkSubjectErasedAsync` with a default implementation that throws
  `NotSupportedException`; documentation of `GetOrCreateCurrentKeyAsync` and `RotateAsync` names
  `KeyScopeErasedException`, and the interface summary states that revocation marks nothing.

## 3. The stores

- [x] 3.1 `src/Stratara.Security/KeyStoreFile.cs`: `ErasedSubjects` (kind, id, marked-at); `Version`
  defaults to 2.
- [x] 3.2 `src/Stratara.Security/EnvelopeFileKeyStore.cs`: `MarkSubjectErasedAsync` under the
  cross-process lock, idempotent; `GetOrCreateCurrentKeyAsync` and `RotateAsync` throw
  `KeyScopeErasedException` before creating anything; loading a file with a newer `Version` fails
  naming both versions and leaves the file unchanged. Tests in `EnvelopeFileKeyStoreTests`:
  mark → request/rotate refused and no key file entry created; never-keyed scope refused; another
  subject unaffected; user mark leaves the user's tenant keys alone; revoke-all then request creates a
  new version; reopen from file still refuses; a second instance on the same file refuses after the
  first marks; a version-3 file refuses to open and its bytes are unchanged; a version-1 file opens.
- [x] 3.3 `src/Stratara.Testing/InMemoryKeyStore.cs`: the same mark and refusal; the same scenarios
  (minus file and process ones) in `InMemoryKeyStoreTests`.
- [x] 3.4 `src/Stratara.Security/DummyKeyStore.cs`: `MarkSubjectErasedAsync` throws
  `NotSupportedException`; covered in `DummyKeyStoreTests`.
- [x] 3.5 `tests/Stratara.Benchmarks/Security/SecureJsonSerializerBenchmark.cs`: its key store stub
  still compiles (default member) — no change expected; confirm.

## 4. The eraser

- [x] 4.1 `src/Stratara.Infrastructure/Security/SubjectEraser.cs`: in both erasures the key-material
  sweep marks the subject before listing and shredding; `NotSupportedException` logs
  `LogEvents.KeyManagement.SubjectNotMarkable` (`112_009`, added to
  `src/Stratara.Diagnostics/LogEvents.cs`) and continues; any other failure is
  `ErasureIncompleteException(ErasurePlane.KeyMaterial, …)`.
- [x] 4.2 Tests in `SubjectEraserTests` / `SubjectEraserKeyCoverageTests`: 1.2 now refused with
  `KeyScopeErasedException`; mark happens before the listing (a store double that records call order);
  a store without the mark warns and still shreds; a rerun after a partial erasure marks again without
  failing.
- [x] 4.3 Encryption paths surface the refusal unchanged: one test each through
  `AesGcmSecureBlobEncryptor.EncryptAsync` and the field-encrypting `SecureJsonSerializer`.

## 5. Documentation

- [x] 5.1 `docs/guides/encrypt-data-setup.md`: the refusal and its exception; a custom key store
  implements `MarkSubjectErasedAsync` (next to the `ListScopesAsync` paragraph); § *Several processes,
  one key store* states the upgrade-together rule and the version refusal.
- [x] 5.2 `docs/guides/tenant-membership.md` and the `ISubjectEraser` remarks: an erased subject gets no
  new key; a store that cannot mark it is named among what the erasure does not cover.
- [x] 5.3 `src/Stratara.Security/README.md`, `src/Stratara.Testing/README.md`: one line each.
- [x] 5.4 `CHANGELOG.md` → *Unreleased*: the guarantee, the behaviour change, the new public types and
  member, and the upgrade note for shared key store files. Marks the next release as minor.

## 6. Verify

- [x] 6.1 `openspec validate keep-an-erased-key-scope-erased --strict`.
- [x] 6.2 Sabotage check: remove the mark check from each store in turn and confirm the 3.2 / 3.3 tests
  go red; restore.
- [x] 6.3 Local gauntlet green (`./scripts/local-gauntlet.sh`).
