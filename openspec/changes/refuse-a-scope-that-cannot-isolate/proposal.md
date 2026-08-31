> **Status:** approved

# Refuse a scope that cannot isolate

## Why

`data-encryption` guarantees that a key scope "combines a sensitivity level with the tenant and user
the data belongs to", that distinct scopes receive distinct keys, and — in its own words — that "a key
shared across scopes would make erasure for one subject destroy another subject's data".

A field marked tenant-scoped on an aggregate that has no tenant breaks that quietly. The tenant and
user come from the event row, and an event row's tenant is a non-nullable value, so a tenant-less
aggregate yields the empty identifier rather than nothing at all. Every such field across every
instance therefore resolves to one scope, and one key:

```
TenantScoped:00000000-0000-0000-0000-000000000000:00000000-0000-0000-0000-000000000000
```

The data is still encrypted. What is lost is the isolation the level's name claims: destroying that
key to erase one subject destroys all of them, so erasure at that level is not available even though
the annotation says it is. A consumer found this after 68 streams had been written that way.

There is already a guard for the same mistake — it refuses to encrypt when the tenant is *null*,
saying the additional authenticated data would not be tenant-bound. It cannot fire on the event path,
because that path never produces null. **The guard is one condition away from the case it was written
for.**

## What Changes

- **BREAKING: encrypting at a level that claims isolation, with no identifying dimension at all, is
  refused outside development.** A tenant-scoped field with no tenant is the case that motivated
  this; a user-scoped field with neither user nor tenant is the same collapse.
- **A coarser dimension than the level names is not refused.** A user-scoped value carrying only a
  tenant resolves to a per-tenant scope: weaker than the name suggests, but it still separates
  tenants, and the framework binds an event's payload to its stream's owner exactly this way. The
  original wording of this proposal treated it as the same defect; the framework's own tests showed
  it is not.
- **In development it warns instead**, so a developer can keep working and still sees it. This
  follows the environment rule the framework already applies to the development key store and to
  broker credentials.
- **Decryption is untouched.** Data already written into the degenerate scope stays readable — a
  refusal on the read path would make existing data permanently unrecoverable, which is a bug rather
  than a guard.
- **A consumer with an aggregate that legitimately has no tenant uses `Confidential`**, which names
  what actually happens: one key for the whole system, no isolation claimed. The documentation says
  so, and that is the migration for anyone this refusal stops.

## Capabilities

### New Capabilities

None. This change introduces no capability.

### Modified Capabilities

- `data-encryption`: adds a requirement that a level claiming isolation must have something to
  isolate by. The capability already guarantees distinct scopes get distinct keys and explains why a
  shared key breaks erasure; nothing said what happens when no dimension is present at all, so the
  answer was silently "share one key with everyone".

## Impact

**Changed**

`src/Stratara.Infrastructure/Security/Serialization/SecureJsonSerializer.cs` — the guard, on the
encryption path only. It gains `IHostEnvironment`, which the assembly already uses elsewhere for the
same kind of rule. The type is `internal`, so the constructor change is not a public break; three
in-repo construction sites are updated with it.

**Consumer impact**

A consumer whose aggregates all carry a tenant sees nothing. A consumer with a tenant-less aggregate
carrying a tenant-scoped field gets an exception on the next write outside development, naming the
field's level, the aggregate and the fix. That consumer was already not getting the isolation the
annotation promised — the change makes an existing silent defect loud, it does not create one.

**Unaffected**

Every stored value, the key store, the erasure sweeps, the blob encryptor, and every other
capability. No ciphertext changes shape, and nothing already written becomes unreadable.
