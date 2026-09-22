## Context

See `proposal.md` — Why. Three defects, all reported against 4.2.0 by a consumer running the
execution model on PostgreSQL, and the reason the last of them is in this change rather than its
own is that all three sit between an append and the consumer that reads it back.

What shapes the approach:

- `PostgresTransactionIdReader.Statements.For` already resolves the table, the bucket column, the
  sequence column and the commit-record column from the EF model, because a consumer's write
  context names them. The wildcard in the statement bodies is the one place that assumes the model
  instead of asking it.
- The reader is a `FromSqlRaw` source that the caller then projects; EF Core composes the statement
  as a subquery and selects the mapped columns of `EventStreamEntry` from it, so any column the
  model maps must exist in the subquery's output. A system column such as `xmin` does not appear in
  `SELECT *`.
- `PartitionCounterInterceptor.InStreamOrder` is the portable counter's answer to the same ordering
  question and has been correct since it shipped. The native reader has no equivalent because its
  order comes from the database.
- `EventSource.SaveChangesAsync` already prepares and validates the bundle before the transaction
  opens (`PrepareEventBundle`, which throws `SessionRequiredException` without a session). There is
  an established place for a pre-commit refusal.

## Goals / Non-Goals

**Goals:**

- The native reader reads under any mapping the framework's own documented conventions can produce.
- A projection may rely on a stream's creating fact arriving first, under either reader.
- A missing command-audit registration is diagnosable from the error alone.

**Non-Goals:**

- Making a silently stalled catch-up visible to a health check. The consumer's report is right that
  a permanently failing catch-up should be more than a log line, but that is the execution model's
  guarantee, not the store's, and it belongs to `orleans-execution` in a change of its own.
- Changing what `AddCommandServices()` registers.
- Removing `IHasRowVersion` from the event entry. A concurrency token on an append-only entry
  guards nothing and is arguably wrong, but the interface is on a public type and removing it is a
  break. It is a candidate for the next major, not for this change.
- Ordering entries of different streams committed together. Only the per-stream order is promised.

## Decisions

### Name the columns from the model rather than `SELECT *`

`Statements.For` gains a projection list built from the entity type's mapped properties, using the
same `Column(...)` helper it already uses for the four columns it names today, plus the commit
record. Every statement selects that list.

*Alternative rejected — `SELECT *, xmin`.* It is the shorter fix and it is what the consumer's
report suggests. It hard-codes one provider's system column into a statement that is otherwise
model-driven, and it fixes exactly the mapping we happened to be told about: a consumer who maps a
shadow property, or a second system column, breaks the same way. The model already knows the
answer; asking it is not more work.

*Alternative rejected — forbid the convention on the event entry.* Documenting "do not apply the
row-version convention to a write context" turns a framework defect into a consumer obligation, and
the convention is applied by one call over the whole model, so obeying it would mean exempting one
entity by hand. The reader is the thing that is wrong.

Evidence: the failure is `42703: column s.xmin does not exist`, reproduced by the consumer on 4.2.0
and reproducible from `PostgresTransactionIdReader.cs:155` and `:163` together with
`ModelBuilderExtensions.ApplyRowVersionConvention`, which maps every `IHasRowVersion` entity —
`EventStreamEntry` among them — with `IsRowVersion()`.

### Order the entries of one commit in the reader, not on the write side

The reader sorts the rows of one transaction the way the portable counter's interceptor sorts the
entries of one save: streams in the order they first appear, each stream's entries in version
order. Both read paths do it — the batch read and the whole-transaction read — because both can
return the entries of one commit.

*Alternative rejected — sort the entries before `SaveChanges` so the identity column follows
version order.* EF Core decides the order of the statements in a batch itself; a sort of the
tracked entries does not bind it, so the fix would hold only by accident and only for the provider
and batch size it was tested under. The ordering the consumer observed was not stable even within
one run — two of five commits were inverted.

*Alternative rejected — have the projection tolerate any order.* That is the workaround a consumer
can choose and did; as a framework answer it would push the ordering problem into every projection
anyone writes.

Evidence: the consumer observed version 2 at sequence 11 and version 1 at sequence 12 in the same
commit, in two of five commits of one end-to-end run on 4.2.0.

### Refuse the append rather than register the auditing behaviours

`EventSource` refuses a session without a causation identity where it already refuses a missing
session, before the transaction opens, naming `AddCommandAuditing()`.

*Alternative rejected — register the command-audit behaviours from `AddCommandServices()`.* It
would remove the failure altogether, and it is what the consumer's report suggests first. Where the
behaviours sit in the pipeline is a consumer's decision — a host composing the execution model
needs the aggregate-grain behaviour innermost — and a registration that appears by itself cannot be
placed. A consumer who wants auditing everywhere adds one line; a consumer who does not would have
to undo it.

*Alternative rejected — make the column nullable.* It would accept an event whose causation is
unknown, which contradicts the provenance requirement this capability already states, and it is a
schema change every consumer would have to migrate for.

Trade-off accepted: a host that appends outside a command — a seeding routine, a platform-initiated
flow — now gets an explicit refusal where it used to get a constraint violation. Both fail; only
one says what to do. The session shape such a flow should use is the subject of a separate change.

## Risks / Trade-offs

- **The named column list drifts from the model.** → It is built from the model at first use and
  cached with the statements, exactly as the four existing column names are; there is no second
  place to keep in step.
- **The projection list is longer than `*` and the statement grows.** → No measurable effect: the
  same columns were already being selected by the outer query.
- **A consumer relies today on receiving a commit's entries in insert order.** → Nothing can rely
  on it: the order was not stable between commits within one run. The change makes an order that
  was accidental into one that is promised.
- **A host that appends outside a command breaks on upgrade.** → It breaks today too, one layer
  down and with a less legible message. The error names the registration that fixes it.
- **The integration test needs a write context that applies the convention.** → It is a test-only
  context in the Orleans integration suite; the existing hosts stay as they are, so the new one
  pins the new guarantee without changing what the others cover.

## Migration Plan

No migration. No schema change, no API change, no configuration change. A consumer who worked
around the reader by unmapping the event entry's row version can remove the workaround after
upgrading and is not required to.
