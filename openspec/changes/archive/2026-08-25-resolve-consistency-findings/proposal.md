> **Status:** proposed

# Resolve the small consistency findings

## Why

A batch of small findings from the backfill, each of which is a decision rather than a repair. They
are grouped because none needs a worklist of its own and each is a one-line answer, but they do need
an answer — leaving them means the next reader re-derives them.

| # | Finding | The decision |
|---|---|---|
| **SG-1** | Saga handler discovery finds non-public methods; projection handler discovery does not | Align, or document why. It surfaces as "my projection handler is never called" with no diagnostic |
| **SG-3** | The coding rule says "no `Stopwatch`", and both workers use `Stopwatch.GetTimestamp()` | Narrow the rule to `new Stopwatch()`, which is what it means |
| **H-1** | `AddCaching` registers a Redis multiplexer, is applied by no composite and consumed by no framework component | Keep and document, or remove |
| **M-1** | `command.duration` is described as end-to-end command latency and is recorded only in the outbox worker — the in-process dispatch path contributes nothing | Narrow the description, or record it on both paths |
| **S-1** | The session middleware's unauthenticated short-circuit tests `?.IsAuthenticated == false`, so a null `Identity` falls through | Tighten to `!= true`. Unreachable through ASP.NET Core's own pipeline |
| **AR-2** | Snapshot evaluation opens two extra transaction scopes per stream on the write hot path | Pass the open transaction down |
| **UC-1** | `CreateChangeSetAsync` rebuilds the aggregate twice per update, not in one transaction | Share the transaction; consider reusing one rebuild |
| **SS-1** | `SettingScope` uses string identifiers, converted at each call site, so a mismatch is a silent miss | One conversion helper, or normalise inside `SettingScope` |
| **SS-2** | The setting provider memoises per scope, not per setting — twenty settings can be eighty reads | Memoise per (scope, name), or load a scope in one read |
| **T-2** | "Endpoints must not bypass CQRS" is a rule for consumers, not a framework guarantee | Already recorded; keep it out of the specs. No action |
| **A-1** | The authorization start-up validator scans `AppDomain.CurrentDomain.GetAssemblies()`, so a guarded type in a not-yet-loaded assembly escapes the fail-fast | Narrow the claim, or scan the entry assembly's dependency closure. Dispatch-time enforcement is unaffected |
| **ES-2** | `ResolveSubjectAsync` consults the stream's recorded tenant only for tenant-scoped aggregates, so a non-tenant aggregate appended under a different session can carry events attributed to different tenants | Decide whether that is intended. If not, extend the stream lookup to all aggregates |
| **TD-1** | For a user with several active memberships and no selection, the tenant claim falls back to "the deterministically first membership"; for a user with none it emits nothing | Decide whether the ambiguous case should also emit nothing. The two were decided differently and the reasoning was never recorded |
| **V-1** | A comment in `ValidationPipelineBehaviorTests` claims discovery requires a public type; `Assembly.GetTypes()` returns non-public types too | Fix the comment |

## What Changes

Small, independent edits. Anything here that turns out to change a stated guarantee gets promoted to
its own change with a spec delta rather than being folded in.
