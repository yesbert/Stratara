# Stratara.Sample.Validation

Shows **`Stratara.Validation`** — the vendor-neutral request-validation pipeline behavior. An
`IValidator<T>` runs *before* the handler, so invalid commands are rejected at the edge and the
handler stays free of defensive guard clauses. No FluentValidation dependency; the contract is
FluentValidation-shape-compatible if you want an adapter later.

## What to look at, in order

1. **`RegisterUserCommand.cs`** — a `ICommand<Guid>` and its handler. Notice the handler has **no
   input checks** — by the time it runs, validation has already passed.

2. **`RegisterUserValidator.cs`** — an `IValidator<RegisterUserCommand>`. Returns a
   `ValidationResult` (never `null` — `ValidationResult.Success` when valid). Demonstrates all three
   severities:
   - `Error` (default) — blocks the request. The pipeline throws `StrataraValidationException`.
   - `Warning` — passes through to the handler, but is logged.
   - (`Info` behaves like `Warning` — non-blocking.)

3. **`Program.cs`** — DI wire-up plus three dispatches: a valid command, a warning-only command
   (still reaches the handler), and an invalid command (blocked, exception caught and its
   `Failures` printed).

## Run it

```bash
dotnet run --project samples/Stratara.Sample.Validation
```

Expected output: the first command is accepted, the second is accepted despite an age *warning*,
and the third is rejected with two `Error` failures (`email.invalid`, `age.minimum`) before the
handler runs.

## Wire-up cheat sheet

```csharp
services.AddSingleton(TracerProvider.Default.GetTracer("Your.App"));  // Mediator depends on OTel Tracer

services
    .AddMediator()
    .AddStrataraValidation()                          // register the behavior FIRST (outermost)
    .AddValidatorsFromAssemblyContaining<Program>()   // discover every IValidator<T>
    .AddCommandHandlersFromAssemblyContaining<Program>()
    .AddQueryHandlersFromAssemblyContaining<Program>();
```

Call `AddStrataraValidation()` **before** any other `AddPipelineBehavior*` registration so
validation runs as the outermost behavior — rejecting invalid requests before authorization,
auditing, or the handler. Only `ValidationSeverity.Error` blocks; `Warning`/`Info` pass through and
are logged.

## Mapping a failure to an HTTP response

`StrataraValidationException` is declared in `Stratara.Abstractions.Validation`, so a consumer's
global exception handler can catch it and map `Failures` to its own error model (e.g. RFC-7807
`ProblemDetails`) **without** referencing the `Stratara.Validation` behavior package.

## Where to go next

- **`Stratara.Sample.CqrsBasics`** — the mediator surface this builds on.
- **`Stratara.Sample.AspNetCoreApi`** — wire the same mediator (and its validation behavior) behind
  HTTP endpoints, where mapping `StrataraValidationException` to a 400 response is the natural step.
