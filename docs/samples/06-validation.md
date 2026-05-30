# Sample 6 — Validation

**Concept**: `Stratara.Validation` — request validation as a mediator pipeline behavior. Invalid
commands are rejected at the edge; the handler stays free of guard clauses.

- **Code**: [`samples/Stratara.Sample.Validation`](https://github.com/yesbert/Stratara/tree/main/samples/Stratara.Sample.Validation)
- **Lines**: ~80
- **Read time**: 5 min
- **What it doesn't have**: persistence, HTTP — it's a console program focused on the validation pipeline.

## What you'll see

1. **`RegisterUserCommand : ICommand<Guid>`** and its handler — the handler has **no input checks**,
   because validation runs first.
2. **`RegisterUserValidator : IValidator<RegisterUserCommand>`** — returns a `ValidationResult`
   (never `null`; `ValidationResult.Success` when valid). It produces all three severities:
   - `Error` for a malformed email or under-age request (blocks),
   - `Warning` for an implausibly high age (passes through, logged).
3. **DI wiring** via `AddStrataraValidation()` + `AddValidatorsFromAssemblyContaining<Program>()`,
   registered **before** the handlers so validation is the outermost behavior.
4. **`StrataraValidationException`** thrown by the pipeline on `Error`-severity failures, caught in
   `Program.cs`, with its `Failures` printed.

## Running

```bash
dotnet run --project samples/Stratara.Sample.Validation
```

Expected output:

```
=== Stratara Validation ===

--- Valid command (alice@example.com, age 30) ---
  Handler ran: registered alice@example.com (age 30) as {guid}
  Accepted: {guid}

--- Warning only (bob@example.com, age 150) — passes through, handler still runs ---
warn: ... 1 non-blocking validation failure(s) observed ... request still dispatched to the handler.
  Handler ran: registered bob@example.com (age 150) as {guid}
  Accepted despite the age warning (Warning/Info never block).

--- Invalid command (not-an-email, age 16) — blocked before the handler ---
  Rejected with 2 failure(s):
    [email.invalid] Email: Email must be a non-empty address containing '@'.
    [age.minimum] Age: Age must be at least 18.

Done.
```

## Key takeaways

- Only `ValidationSeverity.Error` blocks. `Warning`/`Info` reach the handler and are logged.
- `StrataraValidationException` is declared in `Stratara.Abstractions.Validation`, so a global
  handler maps `Failures` to your error model (e.g. RFC-7807 `ProblemDetails`) without depending on
  the behavior package.

See the **[Write a Validator](../guides/write-a-validator.md)** guide for the full walkthrough.
