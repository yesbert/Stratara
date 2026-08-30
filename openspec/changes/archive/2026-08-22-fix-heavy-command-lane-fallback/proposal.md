> **Status:** approved

# Keep a heavy command in its own lane on the fallback path

## Why

`CommandOutboxDispatcher.ResolveTopic` falls back to the shared command topic when a stored
command's recorded type cannot be resolved. A heavy command whose type is absent from the
trusted-type allowlist is therefore re-published from durable storage onto the **interactive**
topic — the exact starvation the heavy lane exists to prevent, reached by the path that only runs
when something already went wrong.

Migration finding **OB-3**. Raised as a cross-check while backfilling `outbox-and-messaging`.

**Traced.** The interactive worker resolves the command's type as its first act and throws when the
type is not registered, so what happens next depends on how the lanes are deployed:

- **Separate processes with separate handler registrations** — the interactive worker cannot resolve
  the type either. It throws, the subscription NACKs, and the command is dead-lettered. It does not
  run on the wrong lane; it does not run at all.
- **One binary deployed twice with different lane flags** — the ordinary arrangement, since the lane
  is a flag on the same worker — the interactive worker has the type registered, resolves it, and
  **runs the heavy command on the interactive lane**. This is the starvation described above.

Both are reachable and neither is acceptable: one silently loses the command to a dead-letter queue,
the other defeats the lane separation. Recording the lane removes the branch entirely.

## What Changes

- Trace what the interactive worker does with an unresolvable heavy command today.
- Persist the lane with the outbox entry, or fail the republish rather than silently redirecting it.
- Test the fallback path for both lanes.

The `outbox-and-messaging` heavy-lane requirement gains a scenario covering the stored-and-republished
path, which it does not currently mention.

## Decisions

**The lane travels in the envelope, not in a new outbox column.** The outbox row carries only the
serialized payload and its type name; the command's own type name already lives inside the envelope
JSON. Adding the flag there costs one optional field with a default, and an envelope written before
the field existed simply deserializes as not-heavy. A column would instead have meant changing
`AddAsync` on the outbox repository SPI — which builds the row itself from a generic payload — and a
schema migration in every consumer repository, for a value only the dispatcher ever reads.

**The flag is deliberately outside the signed canonical form.** The canonical projection covers the
command type name and the session context. The lane is read from the durable outbox row when
republishing and is never read from a message received off the bus, so signing it would protect a
path that does not exist.

**Type resolution stays as a fallback.** A heavy envelope stored by an earlier package version
carries no flag. For those rows the dispatcher still resolves the type as before, which keeps their
routing exactly as good as it is today; only when that also fails do they reach the shared topic. The
guarantee in the spec delta therefore holds for every envelope written by this version onward, and
the upgrade window degrades to current behaviour rather than to something worse.
