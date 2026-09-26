## 1. Reproduce first

- [x] 1.1 `tests/Stratara.Infrastructure.Tests/Security/SubjectEraserKeyCoverageTests.cs`:
  - a value written for tenant T and a user who is no member of T; the tenant's erasure makes it
    unreadable;
  - a user-level value written for user U in a tenant U has left; the user's erasure makes it
    unreadable;
  - a tenant erased once, then its value under a former member's key erased by a second run.

  Confirm all three fail on `main`.
- [x] 1.2 `tests/Stratara.Security.Tests` (file store) and `tests/Stratara.Testing.Tests` (in-memory
  store): `ListScopesAsync` returns every scope that holds a key, and none that was erased.

## 2. The fix

- [x] 2.1 `IKeyStore.ListScopesAsync` with a default implementation that throws
  `NotSupportedException`, fully documented.
- [x] 2.2 `EnvelopeFileKeyStore` and `InMemoryKeyStore` implement it by parsing their stored scope names.
- [x] 2.3 `SubjectEraser` shreds the union of the listed scopes naming the subject and the directory's
  scopes, falling back to the directory's alone on `NotSupportedException`.

## 3. Documentation

- [x] 3.1 `ISubjectEraser` remarks and `docs/guides/tenant-membership.md`: the not-covered list names a
  key store that cannot list its scopes instead of non-members.
- [x] 3.2 `docs/guides/encrypt-data-setup.md`: a custom key store implements `ListScopesAsync`.
- [x] 3.3 `CHANGELOG.md` → *Unreleased*.

## 4. Verify

- [x] 4.1 `openspec validate let-an-erasure-find-every-key-it-names --strict`.
- [ ] 4.2 Local gauntlet green.
