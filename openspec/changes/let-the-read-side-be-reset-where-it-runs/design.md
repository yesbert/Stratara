# Design

## D1 — A reset of its own, beside the guarded write

**Decision.** `IProjectionCheckpointStore` gains `ResetAsync(projection, partition, reader)`, with a
default implementation that calls `SetAsync(..., 0)` so an implementation outside the framework keeps
compiling. The framework's store writes the beginning and takes the row's reader name over.

**Why not exempt position 0 from the guard in `SetAsync`.** `SetAsync` is also how a host seeds and how
a test plants a position; an exemption would make "write 0" mean two things at once, and a seeding that
wrote 0 by accident would silently adopt a foreign row. A verb that says reset is what the rebuild and
the replay mean, and it is what the refusal message can name.

**What stays guarded.** Reading a checkpoint and advancing it still refuse a row held under another
reader: those are the paths where a position from another reader would be read as this reader's.

## D2 — Pausing is an operation that can fail

**Decision.** `StoreReaderPause.PauseAllAsync` starts every pause, observes each one, and — when any
failed — resumes the ones that succeeded and throws naming the partitions it could not pause. The
caller resumes what it holds in a `catch` (best effort, keeping the original exception) and in the
happy path with a resume whose failure is allowed to surface.

**Why not a pauser lease.** A lease, or a pauser with an identity, would close two gaps the counting
pauser leaves open: a caller that dies mid-rebuild, and a resume whose own answer is lost, which
decrements a second time on the retry and can release a pauser another rebuild holds. Both cost a
grain-side timer or a request identity and a spec sentence of their own. This change takes the trade
knowingly: a reader stuck paused until its silo restarts is worse than a reader that resumes once too
early, because the first is invisible and permanent and the second is a rebuild reading live events a
moment too soon, which re-reading repairs.

## D3 — Hybrid is decided before the "already replaced" guard

**Decision.** `AddStoreReaderCore` asks for the hybrid wiring whether or not the model's dispatcher is
already registered: a second registration with `hybrid: true` either finds the kept dispatcher already
in place (nothing to do) or fails naming what is missing. The kept dispatcher keeps its original
descriptor's shape, and a type-shaped registration is registered by type so the container owns its
disposal.

## D4 — A lost wake-up is a log line

**Decision.** `117_121` at debug for one failure with the consumer and partition named. Debug, not
warning: a lost wake-up costs latency and never a fact, and a silo that stops during a commit would
otherwise warn on every shutdown. The nudge target continues over the consumers behind the one that
failed, so one broken target cannot silence a commit.
