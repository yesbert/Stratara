# Tasks

## Resolved

- [x] **SG-1 — the finding does not reproduce.** Projection handler discovery already uses
      `BindingFlags.NonPublic`, in both `ProjectionMethodInvoker` and the DI registration, with
      comments calling it intentional. Saga and projection discovery are aligned. Nothing to change;
      recorded so the next reader does not re-derive it.
- [x] **SG-3 — rule narrowed.** `openspec/config.yaml` said "no Stopwatch"; it means no *allocating* a
      Stopwatch, which is what the project's C# coding guidelines already said.
      `Stopwatch.GetTimestamp()` is fine and is what the framework's own workers use.
- [x] **H-1 — the finding is wrong; `AddCaching` stays.** It is not "consumed by no framework
      component": `RedisOutboxLock` and the projection replay state both take `IConnectionMultiplexer`
      directly. Neither is wired by a composite, so the coupling is invisible until one fails to
      resolve — which is presumably how the finding arose. Both are now named in `AddCaching`'s own
      documentation.
- [x] **M-1 — description narrowed.** `command.duration` is recorded only in the outbox worker, so
      "end-to-end command latency" was wrong: a host dispatching in-process sees an empty histogram,
      one doing both sees half its traffic. It now says what it measures. Recording the in-process
      path would change what the instrument means and is a separate decision, as the task noted.
- [x] **S-1 — tightened to `!= true`.** A null `Identity` fell through the unauthenticated
      short-circuit. Unreachable through ASP.NET Core's own pipeline, but
      `MembershipClaimsTransformation` already wrote it the strict way and the two should not differ.
- [x] **AR-2 — the extra transaction is gone.** `ShouldCreateSnapshot` opened its own transaction per
      stream, on the write hot path, to answer one version lookup the caller's open transaction could
      already serve. It takes the repository now. **Partial:** the second extra scope the finding
      counts comes from `AggregationService.AggregateAsync`, which is the same problem as UC-1 below.
- [x] **SS-1 — one conversion point.** `CandidateScopes` hand-formatted the session's Guids into a
      `SettingScope`; `SettingScope`'s own factories already do exactly that. It uses them now, so
      there is one place that converts.
- [x] **SS-2 — one read per scope.** The finding says the provider memoises per scope rather than per
      setting; it is the other way round. The per-name cache was already there, and the eighty reads
      come from walking the fallback chain — twenty inherited settings over four scopes. Each scope is
      loaded once in full now, so that is four reads. Same fallback order, same results; the 67
      existing tests pass unchanged.
- [x] **T-2 — confirmed, no action.** "Endpoints must not bypass CQRS" is guidance for a consumer's
      own code, not something a consumer of a published package can observe. It stays out of the specs.
- [x] **A-1 — claim narrowed.** The start-up validator scans loaded assemblies, so a guarded type in a
      not-yet-loaded assembly escapes it. Its documentation now says the fail-fast is best-effort and
      why that is tolerable: dispatch-time enforcement is unaffected, because a guarded type always
      goes through the authorizing mediator when it is actually dispatched.
- [x] **V-1 — comment corrected.** Discovery filters on non-abstract and non-interface only;
      `Assembly.GetTypes()` returns non-public types, so an internal validator is discovered too.

## Declined, with the reasoning

- [x] **UC-1 — decided 2026-08-25: not doing it, and the earlier reason for promoting it was wrong.**

      *The correction first.* This was recorded as needing its own change because sharing a
      transaction across the two rebuilds would mean adding a parameter to
      `IAggregationService.AggregateAsync`, a published Tier-A interface. That is not so.
      `AggregationService` and `ChangeSetHandler` are both `internal sealed` in the same assembly, so
      the fix is an internal seam with no consumer-visible surface at all. Not a breaking API change,
      not a major-version item.

      *The decision.* Owner's call: no measured performance problem, so no change. What it would buy
      is one fewer database round trip and one fewer decryption pass **per update command** — three
      round trips saved, roughly 3–15 ms depending on database latency. What it would cost is a
      conditional where the code runs straight today: the single-read path only works when the
      current snapshot sits at or before the command's source version, and otherwise falls back.
      That trade is worth making under load and not worth making at low volume.

      *Scope, for whoever revisits this.* The double fetch exists in exactly one place —
      `ChangeSetHandler.CreateChangeSetAsync`, lines 32 and 36. `AggregateAsync` has only two other
      callers in the framework and each calls it once. So this is one method, one call site, and the
      benefit lands only on the `IUpdateCommand` path.

      *Revisit when* update-command throughput is measured and the round trips show up in it — not
      before.
- [x] **AR-2's remaining half falls under the same decision.** `SnapshotService` opens a nested
      transaction while its caller already holds one, which is the second scope the finding counted.
      It is internal for the same reason and would use the same seam. Declining UC-1 declines this
      too, unless a measurement separates them — snapshot creation runs on a different cadence than
      updates, roughly once per fifty versions per stream, so it could be measured on its own if it
      ever matters.

## Owner decisions still open

- [x] **ES-2 — answered 2026-08-25: no, and the question turned out to be simpler than it looked.**
      It first read as a design trade-off: perhaps a shared aggregate is *meant* to record events per
      writing tenant. Reading the interfaces removed that reading. `ITenantAggregate` adds one member,
      `TenantId`, and exists so a rehydrated aggregate can be compared against the session by
      `AggregateOwnedByTenantAsync` — it describes the shape of the class, not whether the stream has
      an owner. Every entry records a subject regardless, and the framework's own `Tenant` aggregate
      is a plain `IAggregate` whose stream plainly has one owner. So nobody decided that a stream may
      have two owners; an interface is being used as a proxy for something it does not mean.
      Promoted to its own change, `anchor-event-subject-to-the-stream`, because fixing it changes
      recorded data and belongs in a major version.
- [x] **TD-1 — answered 2026-08-25: no claim, and the finding understated it.** The owner's decision
      is that belonging to several tenants stays supported but the active one must be *selected*, not
      guessed. Checking the specification while writing that up turned the question into something
      sharper: the `tenant-directory` requirement already says "otherwise no claim at all", and the
      code returns `active[0]`. So this is not two cases decided differently with the reasoning
      unrecorded — the reasoning was recorded, in the requirement, and the implementation never
      followed it. What let them drift is one hedged scenario, "deterministic rather than arbitrary",
      which a Guid sort satisfies while defeating the requirement's own sentence. Promoted to
      `require-an-explicit-tenant-selection`, which fixes the code and closes the loophole.
