# Let an erasure find every key it names

> **Status:** approved

## Why

A key is named by level, tenant and user together, and a key store erases a key by its exact name.
To erase a subject, the framework has to *know* the names of the subject's keys, and today it derives
them from the directory. A tenant's erasure computes the keys naming the tenant alone and those naming
it together with each current member. A user's erasure computes the keys naming the user alone and
those naming it together with each tenant it currently belongs to.

A key naming a tenant together with a user who is not a member when the erasure runs is therefore
found by neither erasure. It stays readable for good. Such keys are common:

- a user who left the tenant before it was erased, or before they were erased themselves;
- an operator acting in a tenant from outside it: commands and intents are encrypted under the
  acting user, and a platform operator is no member of the customer's tenant;
- a tenant erased before 4.4.0, whose memberships the first run already removed. Running the erasure
  again cannot compute the keys it shared with its former members.

The key store knows every key it holds. The erasure should ask it.

## What Changes

The consumer-visible effect: a tenant's erasure shreds every key naming the tenant, and a user's
erasure every user-level key naming the user, whoever else the key names and whether or not that
subject is still in the directory.

- `IKeyStore` gains `ListScopesAsync`, which lists the scope of every key the store holds. It has a
  default implementation that throws `NotSupportedException`, so an existing custom key store still
  compiles and behaves as before.
- The framework's key stores implement it: the file-backed store and the in-memory store of the
  test-support package.
- The eraser shreds the union of the listed scopes that name the subject and the scopes the
  directory names. A store that cannot list its scopes leaves the eraser with the directory's scopes
  alone, as today. The documentation names that case.
- An erasure run again after an earlier, incomplete one now reaches what the first run missed.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `tenant-directory`: *A subject's erasure can be performed as one operation* says the key material
  includes keys shared with subjects no longer in the directory, and adds scenarios for a former
  member and a repeated erasure.

## Impact

- `src/Stratara.Abstractions/Abstractions/Security/IKeyStore.cs` — `ListScopesAsync` with a default
  implementation.
- `src/Stratara.Security/EnvelopeFileKeyStore.cs`, `src/Stratara.Testing/InMemoryKeyStore.cs` —
  implementations.
- `src/Stratara.Infrastructure/Security/SubjectEraser.cs` — the union of listed and directory scopes.
- `src/Stratara.Abstractions/Abstractions/Erasure/ISubjectEraser.cs`, `docs/guides/tenant-membership.md`
  — the not-covered list names a key store that cannot list its scopes instead of non-members.
- `docs/guides/encrypt-data-setup.md` — a custom key store should implement the listing.
- `CHANGELOG.md`.
