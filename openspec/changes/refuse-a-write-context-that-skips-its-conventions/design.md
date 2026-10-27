## Context

See proposal.md, *Why*. The current state is the 4.5.0 change `read-a-stream-without-walking-the-store`, design D2.
`WriteDbContext<TContext>.ConfigureConventions` adds `SequenceNumberIndexConvention`, a model-finalizing
convention. It is the only place that can see the final key after a consumer's `ApplyGlobalEntityConfiguration()`.
An options-extension plugin would miss the design-time factories (D2). So the indexes depend on a derived context
calling `base.ConfigureConventions`. Entity Framework calls `ConfigureConventions` and then `OnModelCreating` on the
same context instance while it builds the model. That happens once per context type at runtime, and once when
`dotnet ef` builds the design-time model.

## Goals / Non-Goals

**Goals:**

- A context whose override keeps the convention from being added cannot produce a model, so it cannot produce a
  migration either.

**Non-Goals:**

- Adding the convention some other way when `base` is skipped. Design D2 of the 4.5.0 change gives the reasons.
- Checking contexts that do not derive from `WriteDbContext<TContext>`.

## Decisions

### D1 — `ConfigureConventions` leaves a mark, and `OnModelCreating` refuses a model without it

`WriteDbContext<TContext>.ConfigureConventions` sets a private instance flag after it adds the convention.
`WriteDbContext<TContext>.OnModelCreating` checks the flag first and, if it is not set, throws an
`InvalidOperationException`. The message names the context type and says to call `base.ConfigureConventions` in its
override. A derived context has to call `base.OnModelCreating` anyway, or it has no event stream at all, so the
check runs wherever the model can be correct.

*Alternatives rejected:*
- Logging a warning. A context builds its model before any logger the framework could reach is available, and a
  warning in a design-time run is easy to miss. That is how the defect stayed silent before.
- Checking in a finalizing step for the indexes' presence. That would need the very convention that was skipped.
- Checking at runtime in a repository or the hashing worker. That fires after a migration without the indexes has
  already been generated and applied.

*Evidence:* Entity Framework's model source calls both hooks on the instance that triggers the build. Tasks 1.1
and 1.2 check this for the runtime model and for the design-time model, with the in-memory provider and with
Npgsql.

## Risks / Trade-offs

- [A consumer that overrides `ConfigureConventions` without `base` fails at start after upgrading.] → That is the
  intent: before this change the same consumer ran without the indexes. The message says exactly what to add. The
  CHANGELOG's upgrade note says so, and neither known consumer overrides the hook.
- [A context type whose model is built from a cached or compiled model skips both hooks.] → Then neither hook runs,
  and the compiled model carries whatever the build that produced it carried, which already passed the check.

## Migration Plan

A consumer whose context overrides `ConfigureConventions` adds `base.ConfigureConventions(configurationBuilder);`.
Nothing else is needed.
