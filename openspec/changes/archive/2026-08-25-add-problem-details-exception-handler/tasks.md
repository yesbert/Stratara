# Tasks

## Decide first

- [x] **Owner decision, 2026-08-23: option 2, with an expiry.** The new handler is added; the
      existing `UseAuthorizationExceptionTo403()` keeps working and is marked `[Obsolete]` pointing at
      it, and is removed in the next major version. Option 1 — subsuming the middleware outright —
      was the cleaner end state but breaks anyone calling it, and this release is deliberately one a
      consumer takes without a migration plan. Permanent option 2 was rejected too: leaving two
      answers to one question standing forever is what the proposal warned about. An `[Obsolete]`
      warning is the one migration notice nobody misses, because it appears in their own build, and
      the expiry means each consumer migrates exactly once instead of twice.
- [x] **Placement, answered 2026-08-23: `Stratara.ServiceDefaults.AspNetCore`, and it costs nothing.**
      The task asked to check the dependency before defaulting there. All three exception types live
      in `Stratara.Abstractions` — `StrataraValidationException`, `AuthorizationException`,
      `TenantAccessDeniedException` — which the package already has transitively through
      `Stratara.ServiceDefaults`. No dependency on the validation or authorization packages is
      needed, which is exactly the outcome the Tier-A placement of framework exceptions exists to
      produce. Only an explicit `FrameworkReference` on `Microsoft.AspNetCore.App` was added, matching
      `Stratara.Identity.AspNetCore`.
- [x] **Correction to the proposal: there are three failure types, not four.** The proposal counts
      "validation, authorization, permission and tenant-access denial". No permission exception
      exists — a permission denial is thrown as `AuthorizationException`, the same as a role denial
      (`AuthorizingMediator` and `AuthorizingCommandOutboxDispatcher` are the only throw sites). The
      mapping covers all three that exist.

## Implement

- [x] `StrataraProblemDetailsExceptionHandler` maps a validation rejection to `400` with the failures
      grouped by the field each concerns, and both refusals to `403`, in one RFC 7807 shape.
- [x] `AddStrataraProblemDetails()` registers it alongside the ASP.NET problem-details service. A host
      that does not call it converts nothing.
- [x] Everything else returns `false` and propagates untouched.
- [x] `UseAuthorizationExceptionTo403()` marked `[Obsolete]`, naming its replacement, why the two must
      not both be registered, and that it goes in the next major version.
- [x] The existing coverage of the obsolete method is kept — it still ships — with the CS0618
      suppression scoped to that one file so it disappears with the method.

## Tests

- [x] A validation rejection becomes `400` with failures grouped per field, several messages on one
      field kept together.
- [x] The response carries the request path, so a caller can attribute it.
- [x] An authorization refusal and a tenant-access denial each become `403` in the same shape.
- [x] **Four unrelated exception types are not converted** — the negative case the change called out.
      A boundary mapper that converts too much is harder to notice than one that converts too little:
      the symptom is a bug reported as a tidy client error instead of reaching the host's diagnostics.

## Document

- [x] `docs/guides/write-a-validator.md` — the built-in mapping is the default now, and the
      hand-written version is the "if you want your own error model" case, with the note that not
      calling `AddStrataraProblemDetails()` is how you opt out.
- [x] `scripts/check-doc-symbols.py` — the ASP.NET problem-details APIs added to the external
      allowlist, so the fabricated-API gate stays meaningful rather than flagging framework methods.
