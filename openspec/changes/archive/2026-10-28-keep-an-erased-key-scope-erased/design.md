## Context

Every encryption asks the key store for the current key of a scope through
`IKeyStore.GetOrCreateCurrentKeyAsync` — `AesGcmSecureBlobEncryptor.EncryptAsync`, and through it
`EncryptingSettingStore`, and `SecureJsonSerializer.EncryptToWrapperAsync` for field encryption. None
of them catches what the key store throws, so a refusal raised there reaches the writer unchanged.
Evidence: the implementation.

Both framework stores key their state by the string `Level:Tenant:User` and forget a scope entirely on
erasure: `EnvelopeFileKeyStore.EraseScopeAsync` removes the entry and persists only when there was one
to remove; `InMemoryKeyStore.EraseScopeAsync` drops the scope's keys and its current pointer. Neither
`GetOrCreateCurrentKeyAsync` nor `RotateAsync` has anything to check afterwards. Evidence: the
implementation; `EnvelopeFileKeyStoreTests` and `InMemoryKeyStoreTests` assert only that the erased
versions no longer resolve.

`SubjectEraser` shreds a subject's keys as the union of the scopes the directory names and the scopes
`ListScopesAsync` reports — tenant: levels `UserScoped`, `TenantScoped`, `Confidential` naming the
tenant; user: `UserScoped` naming the user (decision record
`2026-09-27-let-an-erasure-find-every-key-it-names`).

## Goals / Non-Goals

**Goals:**
- No key covered by a subject's erasure is created after the erasure, whether or not the scope ever
  held a key.
- The refusal is seen by every process sharing a file-backed store, and survives a restart.
- No breaking change at compile time for a consumer's own `IKeyStore`.

**Non-Goals:**
- Fencing the other erasure planes (settings, API keys, memberships) against re-creation. Those writes
  carry no key material, and a consumer that needs a fence there owns the registry that knows the
  subject is gone.
- Refusing *decryption* of a marked subject's keys before they are shredded. The eraser shreds
  immediately after marking; refusing reads in the gap would add a second check to the hot read path
  for no gain.
- Lifting a mark.

## Decisions

**Mark a subject, not a scope.** The store records erased tenants and users and refuses every scope
that names one, instead of a tombstone per erased scope. A tombstone per scope covers only scopes
that held a key or that the directory named at the time; a combination first written after the
erasure — an operator acting in the tenant from outside it — would still get a fresh key. Owner
decision in the planning conversation of 2026-10-28.
- *Alternative:* a tombstone per exact scope, written by `EraseScopeAsync`. Rejected for the gap
  above; it also needs no new member, which is its only advantage.
- *Alternative:* both. Rejected: a direct `EraseScopeAsync` caller who is not erasing a subject —
  shredding one scope on purpose — would then lose that scope for good, which changes an existing
  member's meaning.

**A new member with a default implementation:**
`ValueTask MarkSubjectErasedAsync(KeySubject subject, CancellationToken cancellationToken = default)`
on `IKeyStore`, defaulting to `NotSupportedException` — the same shape as `ListScopesAsync`, for the
same reasons recorded in `2026-09-27-let-an-erasure-find-every-key-it-names`: every existing
implementation keeps compiling, and the eraser discovers support by calling the member.

**`KeySubject` in `Stratara.Abstractions`, and it owns the coverage rule.** A `readonly record struct`
with `KeySubject.Tenant(string id)` and `KeySubject.User(string id)` factories and
`bool Covers(KeyScope scope)`: a tenant covers any scope whose `TenantId` equals its id; a user covers a
`UserScoped` scope whose `UserId` equals its id. Ids compare `OrdinalIgnoreCase`, as the eraser already
compares them (`SubjectEraser.Names`), so a GUID written in upper case by a consumer is still caught.
Putting the rule on the type gives both stores and any custom store one definition instead of three
copies of "which levels a user's erasure covers".
- *Alternative:* two members, `MarkTenantErasedAsync` / `MarkUserErasedAsync`. Rejected: doubles the
  default-implementation surface and leaves the coverage rule to every implementer.
- The tenant rule includes level `None` while the eraser's listing filter does not. A `None` scope
  states no isolation and is not written with a tenant by the framework; refusing one is harmless,
  and the spec's "a key of any level that names the tenant" stays literally true.

**`KeyScopeErasedException : InvalidOperationException`, carrying `Scope`.** Deriving from
`InvalidOperationException` keeps an existing `catch (InvalidOperationException)` working; the
dedicated type lets a consumer map it to its own "gone" answer. Thrown by `GetOrCreateCurrentKeyAsync`
and `RotateAsync`, before anything is created.

**File store: marks in the file, under the existing lock.** `KeyStoreFile` gains
`ErasedSubjects` — kind, id and the time of marking. `MarkSubjectErasedAsync` takes the cross-process
lock, reloads, adds the mark if absent, and persists through the same atomic-rename path as every
other write. `GetOrCreateCurrentKeyAsync` and `RotateAsync` already reload under the lock before
deciding, so a mark another process wrote is seen without any new synchronisation. The check runs
before `HighestNonRevokedKeyId`, so an existing current key is refused too — between marking and
shredding nothing new is encrypted under it. Evidence: the cross-process tests in
`EnvelopeFileKeyStoreTests` that this change extends.

**File format version 2, and refuse a newer one.** Every write stamps `Version = 2`. Loading a file
whose `Version` is greater than the highest known fails with a message naming both versions and does
not touch the file; a version-1 file loads as before (no marks). The refusal cannot protect against an
*older* release, which never checked the version — it reads a version-2 file, ignores
`erasedSubjects`, and drops it on its next write. Hence the upgrade-together rule in the spec, the
guide and the changelog.
- *Alternative:* keep marks in a sidecar file an older release never rewrites. Rejected: a second file
  to keep consistent with the first under the lock, and an older release would still mint keys for a
  marked subject — the guarantee would fail anyway, only less visibly.

**In-memory store: a set under the existing lock.** `InMemoryKeyStore` keeps the marks in a set
guarded by `_gate`, checked by `GetOrCreateCurrentKeyAsync` and `RotateAsync`. Same exception, so a
consumer's tests see production semantics.

**Development store refuses.** `DummyKeyStore.MarkSubjectErasedAsync` throws `NotSupportedException`
with the same message family as its `EraseScopeAsync` — the spec forbids it to simulate an erasure
operation.

**Eraser: mark first, inside the key-material plane.** In both `EraseUserAsync` and
`EraseTenantAsync` the key-material sweep marks the subject, then lists and shreds. Marking first
closes the race: an encryption that wins the lock before the mark creates a key that the listing then
finds and shreds; one that loses is refused. A `NotSupportedException` from the mark logs a warning
(new id `LogEvents.KeyManagement.SubjectNotMarkable = 112_009`; the file store logs a mark it writes as `SubjectMarkedErased = 112_010`) naming the store and the subject and
the sweep continues, mirroring `KeyScopesNotListable`; any other failure is an
`ErasureIncompleteException` for `ErasurePlane.KeyMaterial`, so a rerun resumes there. A rerun marks
again, which is a no-op.

**`RevokeAsync` is unchanged.** Revoking every version leaves the scope able to create a new one —
revocation is not erasure, and the spec now says so in a scenario.

## Risks / Trade-offs

- [Risk] A process on an earlier release sharing the file drops the marks. → Stated in the spec, in
  `docs/guides/encrypt-data-setup.md` § *Several processes, one key store*, and in the changelog entry
  as an upgrade note. A newer-than-known file is refused from this release on, so the next format
  change is safe in both directions.
- [Risk] A consumer erased a subject and later reused its identity on purpose. → It now fails with a
  named exception instead of silently succeeding. Called out as a behaviour change in the changelog;
  no lift is offered (owner decision: never needed).
- [Risk] A custom key store or a decorator that does not forward the new member falls back silently
  to today's behaviour. → The eraser warns (`SubjectNotMarkable`), and the setup guide asks a custom
  store to implement the mark, next to the existing paragraph on `ListScopesAsync`.
- [Trade-off] Marks accumulate for ever. Each is a kind, an id and a timestamp — small next to the
  wrapped keys a subject held — and erasures are rare.
- [Trade-off] Every key request now checks the marks. The set is scanned linearly against one scope;
  with marks in the hundreds this is far below the cost of the file reload that already precedes it.

## Migration Plan

Upgrade every process that shares a file-backed key store in one step. A version-1 file needs no
migration; it is rewritten as version 2 on the next write. Rollback to an earlier release is possible
— it reads a version-2 file — but loses every mark on its first write, so subjects erased in between
must be erased again after rolling forward.
