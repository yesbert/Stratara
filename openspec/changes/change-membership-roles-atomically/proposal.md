# Change a membership's roles without rewriting the whole membership

> **Status:** approved — 2026-11-05, by the owner's explicit delegation to release 4.7.1 autonomously, given after the proposal was drafted and before any of it was implemented.

## Why

A consumer that gives or takes one role of a membership today has to read the membership, change its
role list and write it back whole. Writing replaces the roles outright, and nothing detects that
somebody else wrote in between, so two role changes that overlap silently lose one of them. NextPA
holds every role on the membership since its change `hold-roles-on-the-membership` and reported this
as NextPA finding F-033 in its `stratara-findings.md`. It is harmless while one operator changes roles
one account at a time; it stops being harmless once a tenant administrator manages people, or once
two products sharing the identity store change the same membership.

## What Changes

- The membership store gains two operations: add roles to one membership, and remove roles from one
  membership. Each changes only the named roles, leaves every other role and the status as they are,
  and returns the membership as it stands afterwards (nothing when there is no membership — neither
  operation creates one).
- On the framework's own stores — the EF Core directory store, which the shared identity store also
  uses, and the in-memory test double — two such changes to the same membership that overlap both
  take effect. Neither is lost, whichever finishes first.
- A membership store a consumer implemented themselves keeps compiling and gets both operations as a
  read followed by a whole write. That fallback carries no guarantee against a concurrent writer, and
  the operation's documentation says so.
- Writing a whole membership is unchanged: it still replaces roles and status, last writer wins.
- No table, column or migration changes. A consumer that hosts the directory in their own context
  adds no migration.
- Additive API: the release is a patch (4.7.1).

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `tenant-directory`: adds the guarantee that single roles can be added to and removed from a
  membership without losing a concurrent change; the whole-membership write stays as it is.
- `test-support`: the in-memory membership store double offers the same two operations with the same
  guarantee.

## Impact

- `ITenantMembershipStore` (Stratara.Abstractions): two new members with default implementations;
  `TenantMembership` gains `WithRolesAdded` / `WithRolesRemoved`, which compute the changed role set.
- `EfTenantMembershipStore` (Stratara.Identity.EntityFrameworkCore): its own atomic implementation.
- `InMemoryTenantMembershipStore` (Stratara.Testing): its own implementation under its lock.
- Docs: the tenant-directory page under `docs/` gets a paragraph on changing single roles.
- `CHANGELOG.md`: entry under 4.7.1.
- Origin: NextPA finding F-033 in `stratara-findings.md` (NextPA repository); no Stratara-side source is
  superseded.
