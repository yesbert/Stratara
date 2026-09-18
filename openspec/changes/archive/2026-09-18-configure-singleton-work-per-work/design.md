## Context

`Add<TWork>` calls `services.AddOptions<SingletonWorkOptions>().Configure(configure)`; `SingletonWorkGrain`
reads `IOptions<SingletonWorkOptions>.Value.KeepAlivePeriod` (`SingletonWorkGrain.cs:32-37`). The
registration already keeps a per-work record in `SingletonWorkRegistrations` (type → name).

## Decisions

### D1 — The callback lives with the work's registration

**Decision.** `SingletonWorkRegistrations` stores each work's callback, replacing an earlier one for the
same type. The grain resolves its work's registration by the name it runs under, copies the silo-wide
`IOptions<SingletonWorkOptions>.Value` into a fresh instance, applies the callback, and validates it
with the existing `OrleansOptionsValidator` rule.

**Why not named options.** `IConfigureNamedOptions` would still accumulate one configure action per call,
so a work registered twice would run both callbacks — the case this change is to close. A registration
record the framework already keeps has "last one wins" for free.

**Why copy the silo-wide value.** A host that set the keep-alive with `Configure<SingletonWorkOptions>`
expects it to apply; with the copy it does, for every work that does not override it.

## Risks / Trade-offs

- [A host that set one work's callback to configure all works] → the others fall back to the silo-wide
  default; the CHANGELOG entry names `Configure<SingletonWorkOptions>` as the way to set them all.
