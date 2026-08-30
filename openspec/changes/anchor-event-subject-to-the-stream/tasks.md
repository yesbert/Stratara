# Tasks

## Implement

- [x] Remove the `ITenantAggregate` condition from `ResolveSubjectAsync`
      (`src/Stratara.Infrastructure/EventSourcing/EventSource.cs`), so the stream lookup runs for
      every aggregate type.
- [x] Correct the `<remarks>` priority list on `EventSource`. It currently reads "Existing
      aggregate's TenantId (for ITenantAggregate streams that already exist)" — stating the
      restriction as though it were intended.
- [x] Leave `ITenantAggregate`, `AppendOnBehalfOfAsync` and the batch cache untouched. This change
      stops misusing the interface; it does not redefine it.

## Tests

- [x] A plain `IAggregate` stream appended from a second tenant's session keeps its first owner.
      This is the test that would have caught the defect, and there is currently none like it.
- [x] The same for an `ITenantAggregate`, asserting the behaviour did not regress for the case that
      already worked.
- [x] `AppendOnBehalfOfAsync` still overrides the stream's owner, for one event only.
- [x] A brand-new stream still resolves from the creation event, then from the session — the change
      must not make a first append fail.
- [x] The failure path is unchanged: no explicit subject, no history, no creation event, no session
      tenant still fails, naming the three ways to supply one.
- [x] **The erasure consequence, end to end.** Append to one stream from two tenants under the old
      shape, then assert under the new shape that a single tenant erasure covers the whole stream.
      This is the reason the change exists and should not rest on a unit test of the resolver alone.

## Measure

- [x] Quantify the added write-path read rather than asserting it is negligible. The lookup now runs
      for aggregates that skipped it, bounded by the per-batch `_streamSubjects` cache — at most once
      per stream per batch, and only when the stream already exists. Record the measured figure in
      this change so `AR-2` and `UC-1`, which reduce reads on the same path, can be weighed against it.

## Rollout

- [x] CHANGELOG entry as **BREAKING**, with the migration note. Nothing stops compiling; events
      simply start being attributed differently, so a consumer exploiting the old behaviour gets no
      compile-time warning.
- [x] State plainly that **existing mixed-ownership streams are not repaired.** Their recorded
      entries keep the owners they were written with, because the store does not rewrite recorded
      events. A consumer with such a stream still has an incomplete erasure, and needs to know that
      rather than infer the change was retroactive.
- [x] Note that the framework's own `Tenant` aggregate is affected — it is a plain `IAggregate`, so
      its own behaviour changes too, not only a consumer's.
