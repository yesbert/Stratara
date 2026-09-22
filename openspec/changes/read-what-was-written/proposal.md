# Read what was written

> **Status:** approved

## Why

A consumer running 4.2.0 on PostgreSQL adopted the Orleans execution model and found that no
projection ever saw a single event. The native commit-order reader reads the event table with
`SELECT *`, and a write context that applies the framework's own row-version convention maps
`EventStreamEntry.RowVersion` onto PostgreSQL's `xmin` system column, which `SELECT *` does not
return. Every catch-up fails with `42703`, retries for ever, writes no checkpoint, dead-letters
nothing, and leaves the host reporting healthy while every write succeeds. The execution model is
the framework's headline capability and it can be switched off entirely by a convention the
framework itself recommends.

Two further defects surfaced in the same adoption, both in the same area and both cheap to fix
alongside: entries of one commit reach a store-reading projection out of stream order, and an
append without the command-audit pipeline fails with a raw constraint violation instead of a
message naming what is missing.

## What Changes

- The native PostgreSQL commit-order reader names the columns it reads, taken from the model,
  instead of `SELECT *`. A consumer convention that maps a property onto a system column, or onto
  any column the reader does not know about, no longer breaks the read.
- The native reader hands the entries of one commit to a consumer in stream order — the streams in
  the order they first appear, each stream's entries in version order — which is what the portable
  counter already does. A projection may rely on a stream's creating fact arriving before the
  facts that follow it, whichever reader is in use.
- Appending to a stream without a causation identity is refused before anything is committed, with
  a message naming the registration that supplies one. Today the append reaches the database and
  fails there with a not-null violation that reads like a schema fault.
- The command-handler guide states that appending requires the command-audit registration, and the
  session-context concept page stops showing a session that cannot append.

No API is removed and no signature changes. `AddCommandServices()` continues not to register the
command-audit behaviours: where they sit in the pipeline is a consumer's decision, and a host that
composes the execution model depends on that order.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `event-sourcing-store`: the commit-order requirement gains the reader's tolerance of a consumer's
  column conventions; the version-order guarantee within a stream becomes reader-neutral instead of
  applying only to the portable counter; appending gains an explicit refusal when the session
  carries no causation identity.

## Impact

- `src/Stratara.Orleans.EntityFrameworkCore/CommitOrder/PostgresTransactionIdReader.cs` — the two
  read statements and the in-memory ordering.
- `src/Stratara.Infrastructure/EventSourcing/EventSource.cs` — the refusal before the commit.
- `tests/Stratara.Orleans.EntityFrameworkCore.IntegrationTests` — a PostgreSQL host whose write
  context applies the row-version convention, and a commit that appends two versions of one stream.
  The suite's existing hosts apply no convention, which is why it never saw the defect.
- `docs/guides/write-a-command-handler.md` and `docs/concepts/session-context.md` — the causation
  requirement, and an example that runs.
- Consumers that worked around the reader by unmapping `EventStreamEntry.RowVersion` can drop the
  workaround after upgrading; nothing forces them to.
- Nothing is dissolved or superseded by this change.
