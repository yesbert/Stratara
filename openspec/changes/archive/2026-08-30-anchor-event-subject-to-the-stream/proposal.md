> **Status:** approved

# Anchor an event's subject to its stream, not to the session that wrote it

## Why

An event's **subject** is its data owner: the tenant whose key encrypts its payload and whose erasure
reaches it. The store records one on every entry, for every aggregate.

`ResolveSubjectAsync` consults the stream's already-recorded owner only when the aggregate implements
`ITenantAggregate`. For any other aggregate the resolution falls through to the session, and the
session belongs to whoever happens to be writing.

**That condition tests the wrong thing.** `ITenantAggregate` adds exactly one member —
`Guid TenantId { get; set; }` — and it exists for exactly one purpose: so a rehydrated aggregate
carries its tenant as a property, letting `AggregateOwnedByTenantAsync` compare it against the
session and refuse a cross-tenant read. It is a statement about the *shape of the class*, not about
whether the stream has an owner. Every stream entry has one either way.

The framework's own `Tenant` aggregate is `Tenant : IAggregate` — a tenant does not belong to a
tenant — and its stream obviously has a single owner. So this is not an exotic case; it is the
ordinary one.

### What it costs today

A `Document` that is a plain `IAggregate`:

1. Alice, in tenant A, creates it. The entry is attributed to A.
2. Bob, in tenant B, appends to the same stream. Not a creation event, not an `ITenantAggregate`, so
   the stream lookup is skipped and the session wins. That entry is attributed to **B**.

One stream, two owners. Two consequences follow, and the second is the sharp one:

- **Erasure becomes incomplete.** Erasing tenant A shreds A's key; Bob's entries on the same stream
  stay readable, and vice versa. Neither erasure covers "the document".
- **The aggregate can stop rehydrating at all.** Events are decrypted with the subject recorded on
  each entry. Once A's key is shredded, A's entries no longer decrypt — so rebuilding the document
  fails, *including for Bob*. A lawful erasure in one tenant breaks a shared aggregate for everyone.

No one decided this. There is no design note choosing per-event ownership for shared aggregates —
there is an interface being used as a proxy for something it does not mean.

## What Changes

The consumer-visible effect: once a stream has a recorded owner, every later event on that stream
carries the same owner, whatever session appends it. Subject resolution stops depending on which
interface the aggregate class implements.

- The `ITenantAggregate` condition is removed from subject resolution, so the recorded owner of an
  existing stream wins for every aggregate type.
- Consumers that genuinely want an event attributed to a different subject keep the explicit route,
  `AppendOnBehalfOfAsync`, which already outranks everything. The difference is that shared ownership
  becomes something a consumer states rather than something the absence of an interface produces.
- The `event-sourcing-store` requirement covering subject resolution is amended: it currently
  describes the priority order with the `ITenantAggregate` restriction as if it were intended.

**Breaking**, and deliberately so: a consumer relying on today's behaviour — a plain `IAggregate`
appended from several tenants — sees later events attributed to the stream's first owner instead of
the acting session. That is the fix, but it is a change of recorded data, so it belongs in a major
version.

## Capabilities

### Modified Capabilities

- `event-sourcing-store`: the requirement describing how an event's subject is resolved drops the
  restriction to tenant-scoped aggregates, and states that a stream's recorded owner is stable.

## Impact

- `src/Stratara.Infrastructure/EventSourcing/EventSource.cs` — `ResolveSubjectAsync` and the
  `<remarks>` block documenting the priority order, which states the restriction as intent.
- `openspec/specs/event-sourcing-store/spec.md` — the subject-resolution requirement.
- Resolves **ES-2**, carried as an open decision in `resolve-consistency-findings`. That change
  records the finding; this one is where it is answered.
- No package, tier, dependency or wire-format change. `ITenantAggregate` itself is untouched — it
  keeps its one member and its one purpose.
