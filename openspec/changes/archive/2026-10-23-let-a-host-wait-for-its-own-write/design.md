## Context

See `proposal.md`, *Why*. Read on `main` at `6567daf`:

- `ProjectionProgress.MarkAsync()` reads `ICommittedPositionReader.CommittedHeadAsync(partition)` for every partition
  and builds `StoreMark(readerName, positions)`.
- `WaitAsync` waits only for partitions whose position in the mark is above 0
  (`Enumerable.Range(0, partitionCount).Where(partition => mark.Positions[partition] > 0)`). A partition at 0 counts
  as reached. That is how a partition with nothing committed is already handled.
- A stream's partition is `PartitionMap.PartitionOf(BucketCalculator.GetBucketId(streamId), partitionCount)`, which
  is the mapping the readers and the bundle dispatcher use.

## Decisions

### D1: A mark of streams is the committed head of their partitions and 0 elsewhere

`MarkAsync(streamIds)` computes the distinct partitions of the streams and reads `CommittedHeadAsync` for those only.
It returns a `StoreMark` with the same reader name and partition count, with 0 in every other partition. The wait
needs no change, because a partition at 0 is reached. A reader applies a partition in position order and never skips,
so the projection still passes every earlier entry of the touched partitions, whatever stream they belong to. The
mark is narrower in partitions, not within one.

*Alternative rejected: a mark returned by `SaveChangesAsync`.* The event source is in a lower tier than the execution
model and knows no positions, and the native reader's position is the transaction's id, which the save would have to
read back. A mark of the streams taken after the save covers the same entries and needs no change to the store.

*Alternative rejected: a mark of exactly the commit's own positions.* It would let the wait answer before the reader
passed the entries below them, but the reader cannot apply an entry without passing those, so nothing would be
gained.

### D2: The interface member has a default that answers the global mark

`IProjectionProgress` is public, and an implementation outside the framework would otherwise stop compiling. The
default `MarkAsync(streamIds) => MarkAsync()` keeps the promise "covers every entry committed to those streams",
because the global mark covers more.

### D3: Review findings

*As built after `/code-review high`.* The delta modifies the existing requirement instead of adding one beside it, so
the spec no longer says that every mark constrains every partition. A mark of no stream is refused with
`ArgumentException` rather than answered by an all-zero mark, which would read as reached at once. A mark of a stream
with nothing committed is reached at once only where its partition holds nothing, and the scenario says so. Both
marks share one builder. The tests assert that the mark constrains the written stream's partition and no other, and
that the write is in the read model. *Kept:* the stream-to-partition expression is not lifted into a new public
`PartitionMap` member, because that would be API for an internal convenience.

## Risks / Trade-offs

- **[A host takes the stream mark for a decision that needs everything]** → The guide says when the global mark is
  the right one, and the uninstall example the consumer gave is named there.
