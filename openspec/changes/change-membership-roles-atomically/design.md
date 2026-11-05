# Design

## Context

See `proposal.md` for the motivation. What constrains the approach:

- The relational membership store is provider-neutral. It references EF Core relational, not a
  provider, and its tests run on SQLite. The shared identity store runs it on PostgreSQL.
- The roles are one column per membership holding a JSON array, through a value converter. There is
  no version and no concurrency token on the entry.
- A consumer may host the directory's tables in their own context, so they own the migrations for it
  (tenant-directory, "The directory tables can be hosted in an existing context"). Any change to the
  entry's model is a migration the consumer has to write.
- The store obtains its context in one of two ways: it shares the request's context, which is the
  default, or it uses a fresh one per operation. On the shared context, `SetMembershipAsync` calls
  `SaveChangesAsync`, which also commits whatever the consumer has left unsaved.
- `ITenantMembershipStore` is a public interface that consumers may implement themselves.

## Goals / Non-Goals

**Goals:**
- Add and remove single roles atomically on the shipped stores, with no change to the model or the
  schema.
- Keep every existing `ITenantMembershipStore` implementation compiling.

**Non-Goals:**
- Protecting `SetMembershipAsync` against concurrent writers. It stays last-writer-wins, as the spec
  requirement "Writing a membership replaces it wholly" states.
- A version on `TenantMembership`. That would change the record's shape, and with it every consumer
  that constructs one.
- Changing the status through the new operations.

## Decisions

### D1 — Compare-and-swap on the roles column, retried a bounded number of times

For each attempt, the EF store:

1. reads the entry untracked;
2. computes the new role list;
3. if the list is unchanged, returns without writing;
4. otherwise issues one `ExecuteUpdateAsync` that sets `Roles` to the new list, filtered by user,
   tenant **and** `Roles == <the list it read>`;
5. if one row is affected, the change landed. If none is, a concurrent writer changed the roles (or
   removed the membership), and it starts again from step 1.

It gives up after 5 attempts and throws `InvalidOperationException`, whose message names the user and
the tenant. The value converter turns the comparison into a comparison of the stored JSON text with
the serialized list that was read. The serializer is deterministic, so equal lists produce equal text.

*Alternatives rejected:*
- **A concurrency token or version column.** It changes the model, so every consumer who hosts the
  directory in their own context would need a migration. That is too much for a patch. It would also
  make `SetMembershipAsync` start throwing on a conflict, which changes its behaviour.
- **A row lock (`SELECT … FOR UPDATE`) or a serializable transaction.** Both depend on the provider.
  On the shared context they would also have to deal with a transaction the consumer already opened.
- **Provider-native array or JSON operators** (PostgreSQL `jsonb` functions). Not portable, and the
  column is text.

*Evidence:* `EfTenantMembershipStoreRoleTests` (SQLite, with a concurrent writer injected between the read
and the update) can only pass if EF translates the equality on the converted column into a comparison with
the converted parameter. On PostgreSQL,
`Two_hosts_adding_different_roles_to_one_membership_at_once_keep_both` can only pass if it does.

### D2 — `ExecuteUpdateAsync`, not the change tracker

`ExecuteUpdateAsync` runs immediately and commits nothing else. On the shared context it therefore
does not flush the consumer's unsaved changes, unlike `SetMembershipAsync`. It also takes part in a
transaction the consumer opened on that context. That is the same rule the existing
`RemoveMembershipAsync` (`ExecuteDeleteAsync`) already follows.

A `TenantMembershipEntry` the consumer has tracked on the shared context is not refreshed. The read
in step 1 is untracked, so it is never used as the basis of a change.

### D3 — Role semantics

- Roles are compared ordinally, the way they are stored and evaluated today.
- When roles are added, the existing roles keep their order and the new ones are appended in the
  order given. Duplicates in the input count once.
- Removing every role leaves the membership in place with an empty list. Removing a membership is
  still `RemoveMembershipAsync`.
- A null or empty role collection is a no-op that returns the current membership. A null or
  whitespace role name throws `ArgumentException`.
- The status is not touched. Changing the roles of a non-active membership is allowed, and those
  roles confer nothing until the membership is active (spec: "An invited membership confers nothing").

### D4 — The API shape

```csharp
Task<TenantMembership?> AddRolesAsync(Guid userId, Guid tenantId, IReadOnlyCollection<string> roles,
    CancellationToken cancellationToken = default);
Task<TenantMembership?> RemoveRolesAsync(Guid userId, Guid tenantId, IReadOnlyCollection<string> roles,
    CancellationToken cancellationToken = default);
```

Both return the membership as it stands after the operation, or `null` where none exists. A caller
who needs the result (for example, to re-issue claims) therefore does not have to read it again.

Both are default interface methods on `ITenantMembershipStore`, implemented as `GetMembershipAsync` +
`SetMembershipAsync`. Their XML docs state that this fallback gives no protection against a
concurrent writer and that an implementation should override it. `EfTenantMembershipStore` and
`InMemoryTenantMembershipStore` implement both as public members, so callers that hold the concrete
type see them too.

The role arithmetic of D3 lives once, on the record, as `TenantMembership.WithRolesAdded` and
`WithRolesRemoved`. They return the same instance when nothing changes, which is how all three
implementations decide not to write. They are public, so a consumer who overrides the fallback uses
the same semantics. This was added during implementation: without it the arithmetic would be copied
into three assemblies, because Abstractions grants no `InternalsVisibleTo`.

The EF store also updates the roles of a `TenantMembershipEntry` the shared context already tracks,
both current and original value. Otherwise a later `SetMembershipAsync` on that context would compare
against the stale tracked roles, find "no change" and skip its `UPDATE`. The test
`A_whole_write_after_a_role_change_on_the_same_context_is_stored` fails without this.

The in-memory double does read, change and write under its existing `_gate`. It never yields inside
the lock, so it is atomic by construction.

### D5 — Version: patch

Adding interface members with default implementations is additive and breaks no implementer, so the
version goes to 4.7.1 (the project's rule: additive API is a patch). The bump and the tag are
separate approvals and are not part of this change.

## Risks / Trade-offs

- [EF does not translate the converted-column equality the way D1 assumes] → Task 1.1 checks this
  first. The fallback is to compare against the stored text through the model's column mapping in a
  raw SQL `UPDATE`, which is still portable, with the table and column names taken from the model
  rather than hard-coded.
- [Two writers keep colliding and one exhausts its 5 attempts] → It throws instead of losing the
  change, as the spec requires. Role changes are rare and human-paced, so in practice this means
  something is wrong.
- [Roles text written outside the store, e.g. `[ "a" ]` seeded by hand, never matches the re-serialized
  list, so every attempt misses] → The store notices that no re-read showed a different role set and
  throws `InvalidOperationException`, naming `SetMembershipAsync` with the wanted role set as the way out.
  That write stores the canonical form. Test: `Roles_stored_in_a_foreign_form_fail_with_a_way_out_instead_of_a_concurrency_claim`.
- [The consumer edited the roles of a tracked entry on the shared context and has not saved] → The
  tracked-entry refresh leaves a modified `Roles` alone, and it inspects only that entry, not the whole
  context. Test: `A_role_change_leaves_a_tracked_entry_the_consumer_edited_but_did_not_save_alone`.
- [A concurrent `SetMembershipAsync` overwrites a role change that just landed] → This is
  last-writer-wins by contract. The operation docs say that whole writes and single-role changes on
  the same membership do not protect each other.
- [The fallback for a consumer's own store is not atomic] → It is documented on the interface
  members, which is what the spec requires.

## Migration Plan

None. No schema change and no behaviour change to existing members. Consumers adopt the new members
when they want them. NextPA replaces its read-modify-write once it moves to 4.7.1.
