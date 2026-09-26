## Context

`SubjectEraser` builds the key scopes to erase from the directory (`GetMembershipsAsync`,
`GetMembersAsync`) and calls `IKeyStore.EraseScopeAsync` for each. Both framework stores key their state
by the string `Level:Tenant:User` and erase by exact match. Evidence: the implementation, and the review
of #162, which listed the writers whose scopes name a non-member: `CommandEnvelopeMapper`,
`AggregateGrainBehavior`, `IntentRecorder`.

## Goals / Non-Goals

**Goals:**
- An erasure reaches every key naming the subject that the key store holds.
- No breaking change for a consumer's own `IKeyStore`.

**Non-Goals:**
- Prefix or pattern erasure inside the key store. Listing is enough, and it keeps erasure one call per
  scope, as the store already supports.

## Decisions

**A listing member with a default implementation, not a new interface.** A default interface member
keeps every existing implementation compiling. It also lets the eraser discover support by calling
the member, without a type check against a second interface.
- *Alternative:* a separate `IKeyScopeCatalog`. Rejected: a second registration to keep in step with the
  key store, and a decorator around the key store would hide it.

**Union, not replacement.** The eraser shreds the listed scopes that name the subject together with the
scopes the directory names. Erasing a scope that holds no key is a no-op. The union keeps today's
coverage wherever a listing is incomplete, for example when a consumer-built scope contains the
separator character and cannot be parsed back.

**Parse the scope back from the stored name.** Both framework stores keep only the `Level:Tenant:User`
string. The level is what precedes the first colon, the user what follows the last, and the tenant is
in between. That is exact for every scope the framework writes, because a GUID contains no colon. A
consumer-built scope whose user contains a colon would be listed wrongly; the union above keeps
today's coverage for it.
- *Alternative:* add the parts to the file format. Rejected for now. A file shared with an older
  process would lose them on its next write, so parsing would still be needed.

**A list, not a stream or a filter.** Erasure is rare, and a key store holds wrapped keys, not data.
Listing everything keeps the member simple to implement for any store.

**Warn on the fallback, and reject an empty id.** A decorator around a key store that does not forward
the new member inherits the default and falls back silently. The eraser therefore logs a warning
(`LogEvents.KeyManagement.KeyScopesNotListable`) naming the store. An empty id keys the system actor
and data written with no tenant; with the listing, erasing it would shred those for every tenant, so
it is refused.

## Risks / Trade-offs

- [Risk] A custom key store that does not implement the listing keeps today's gap. → The erasure
  documentation and the encryption setup guide say so. The eraser treats `NotSupportedException` as
  "cannot list" and falls back without failing.
- [Risk] Listing reads the whole key file. → Erasure is rare and the file holds only wrapped keys.
